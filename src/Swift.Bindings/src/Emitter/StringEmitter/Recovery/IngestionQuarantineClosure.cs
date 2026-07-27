// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The proven-closure half of the ingestion DEGRADE plane. The parser quarantines a malformed bindable
/// type (a struct/enum/class/protocol whose load-bearing mangled name is absent) by marking
/// <see cref="TypeDecl.IsIngestionQuarantined"/> and withholding it from the type database. This walk
/// takes those quarantined types and computes the full set of retained declarations that depend on them,
/// so emission can tombstone the whole closure through the same recovery-withdrawal seed the wrapper
/// verify-recover loop uses — recorded with a distinct <see cref="EmitterFaultOrigin.IngestionWithdrawal"/>
/// origin so a reader can tell an ingestion withdrawal from a compile-driven one.
/// </summary>
/// <remarks>
/// <para>
/// The disposition policy is consensus-locked: a malformed type record quarantines the type PLUS every
/// retained inheritance/conformance/stored-field/enum-payload edge that structurally depends on it (those
/// retained TYPES are withdrawn whole, because a type whose layout embeds a quarantined type is itself
/// indeterminate), and every retained signature edge — a method/property/subscript/operator/free-function
/// whose signature reaches a withdrawn type — is withdrawn as a leaf so its healthy siblings survive.
/// </para>
/// <para>
/// Withdrawal enumerates exactly the edges the policy lists, and the residual scan re-checks the RETAINED
/// surface for one specific channel withdrawal cannot model: a retained type's own generic where-clause
/// constraint on a quarantined type, which cannot be withdrawn as a leaf and whose whole-type withdrawal
/// needs generic-context closure reasoning this plane does not own. A residual there means the module
/// fails closed (SWIFTBIND120) rather than shipping a compile-clean/runtime-wrong binding.
/// </para>
/// <para>
/// That scan is a PARTIAL backstop, not a completeness proof: it establishes only that the one modelled
/// residual channel is clear, and a clean scan is not evidence that no other channel exists. Soundness on
/// the rest of the surface comes from the consumers, not from this plane — every emission plane that can
/// reach a withdrawn type consults the withdrawal set itself (the in-emission planes read the ambient
/// poison list; the planes that run after the emission attempt is disposed — the module database, the
/// Swift type-ownership manifest, the SwiftUI bridge's parameter walk — are handed the withdrawal set
/// explicitly). A new plane that resolves types from the raw module tree or the type database has to join
/// that set of consumers; nothing here will catch it if it does not.
/// </para>
/// </remarks>
internal static class IngestionQuarantineClosure
{
    /// <summary>
    /// Computes the withdrawal closure of every ingestion-quarantined type in <paramref name="moduleDecl"/>.
    /// </summary>
    /// <param name="moduleDecl">The parsed module whose quarantined types seed the closure.</param>
    /// <param name="currentModuleName">
    /// The module being emitted, so a cross-module short-name collision never matches a withdrawn type.
    /// </param>
    /// <param name="logger">Diagnostics sink for the per-withdrawal trace.</param>
    /// <param name="dependencyQuarantinedNames">
    /// Module-qualified names (e.g. <c>IngestionBase.BaseSignal</c>) of types quarantined in a DEPENDENCY
    /// module and withheld from the type database. A primary construct that inherits, conforms to, or names
    /// one of these across the module boundary is as indeterminate as one reaching a locally-quarantined
    /// type, so these names seed the closure's reachability walk exactly like a locally-withdrawn name. They
    /// are NEVER themselves emitted as withdrawal units (they belong to another module and are not in
    /// <paramref name="moduleDecl"/>) — they only poison the names that primary declarations are tested
    /// against. Must be module-qualified: a cross-module inheritance/conformance reference is matched by its
    /// full qualified form, never a short name (a short-name seed would false-match a same-named local type).
    /// </param>
    /// <returns>
    /// The withdrawal units to seed emission with, whether the closure is provably complete, and — when it
    /// is not — the human-readable reason the module must fail closed.
    /// </returns>
    public static IngestionQuarantineResult Compute(
        ModuleDecl moduleDecl,
        string currentModuleName,
        ILogger logger,
        IReadOnlyCollection<string>? dependencyQuarantinedNames = null)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(currentModuleName);
        ArgumentNullException.ThrowIfNull(logger);

        var allTypes = new List<TypeDecl>();
        FlattenTypes(moduleDecl.Types, allTypes);

        // External poison: the module-qualified names of dependency-module types quarantined at ingestion.
        // These seed reachability but never become withdrawal units of THIS module.
        var externalNames = new HashSet<string>(StringComparer.Ordinal);
        if (dependencyQuarantinedNames is not null)
        {
            foreach (var name in dependencyQuarantinedNames)
            {
                if (!string.IsNullOrEmpty(name))
                    externalNames.Add(name);
            }
        }

        // 1. Seed the withdrawn-type set from the parser's quarantine marks, pulling every nested
        //    descendant of a quarantined type in with it — a nested type of a withheld parent is
        //    unreachable and must not be emitted on its own.
        var withdrawnTypes = new HashSet<TypeDecl>(ReferenceEqualityComparer.Instance);
        foreach (var type in allTypes)
        {
            if (type.IsIngestionQuarantined)
                AddWithNested(type, withdrawnTypes);
        }

        // Nothing to do only when there is neither a locally-quarantined type NOR a dependency-quarantined
        // name a primary construct could reach across the module boundary.
        if (withdrawnTypes.Count == 0 && externalNames.Count == 0)
            return IngestionQuarantineResult.Empty;

        // 2. Grow the withdrawn set by structural edges to a fixpoint: a retained type whose superclass,
        //    protocol inheritance, protocol conformance, a STORED field, or an enum associated-value
        //    payload reaches an already-withdrawn type (local OR dependency-quarantined) has an
        //    indeterminate layout/identity and is withdrawn whole (its nested descendants with it). The set
        //    only ever grows and is bounded by the type count, so the loop terminates.
        bool grew;
        do
        {
            grew = false;
            var withdrawnNames = BuildNameSet(withdrawnTypes, externalNames);
            foreach (var type in allTypes)
            {
                if (withdrawnTypes.Contains(type))
                    continue;
                if (StructurallyReaches(type, withdrawnNames, currentModuleName))
                {
                    AddWithNested(type, withdrawnTypes);
                    grew = true;
                }
            }
        }
        while (grew);

        var finalNames = BuildNameSet(withdrawnTypes, externalNames);
        var withdrawals = new HashSet<RecoveryUnitId>();

        // 3a. Withdraw every type in the closure at its whole-type surface.
        foreach (var type in withdrawnTypes)
        {
            var unit = RecoveryUnitId.Create(DeclIdFactory.ForType(type), RecoveryScope.TypeSurface);
            if (withdrawals.Add(unit))
                RecordWithdrawal(type, unit, WithdrawalReason.QuarantinedTypeOrClosure, finalNames, currentModuleName, logger);
        }

        // 3b. Withdraw every retained member and free declaration whose SIGNATURE reaches a withdrawn
        //     type, as a leaf/accessor group so its healthy siblings on the same type survive.
        foreach (var type in allTypes)
        {
            if (withdrawnTypes.Contains(type))
                continue; // the whole type is already withdrawn; its members go with it.
            CollectSignatureWithdrawals(type, type, finalNames, currentModuleName, withdrawals, logger);
        }
        CollectModuleLevelWithdrawals(moduleDecl, finalNames, currentModuleName, withdrawals, logger);

        // 4. Backstop scan: re-check the RETAINED surface for the one residual channel withdrawal does
        //    not model — a retained type's own generic where-clause constraint on a withdrawn type.
        //    This clears that channel only; it does not prove the closure complete against channels
        //    nobody has modelled. The other planes stay sound by consulting the withdrawal set below.
        // Module-qualified names of the LOCAL withdrawn types, for the emission planes that run
        // outside the ambient emission attempt and therefore cannot read the poison list.
        var withdrawnTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in withdrawnTypes)
        {
            var qualified = type.SwiftTypeName?.ModuleQualifiedName;
            if (!string.IsNullOrEmpty(qualified))
                withdrawnTypeNames.Add(qualified!);
        }

        var unprovenReason = FindUnprovenResidual(allTypes, withdrawnTypes, finalNames, currentModuleName);
        if (unprovenReason is not null)
        {
            return new IngestionQuarantineResult(
                Withdrawals: withdrawals,
                ProvenComplete: false,
                UnprovenReason: unprovenReason,
                WithdrawnTypeNames: withdrawnTypeNames);
        }

        return new IngestionQuarantineResult(
            withdrawals, ProvenComplete: true, UnprovenReason: null, WithdrawnTypeNames: withdrawnTypeNames);
    }

    private static void FlattenTypes(IEnumerable<TypeDecl> types, List<TypeDecl> sink)
    {
        foreach (var type in types)
        {
            sink.Add(type);
            FlattenTypes(type.Types, sink);
        }
    }

    private static void AddWithNested(TypeDecl type, HashSet<TypeDecl> sink)
    {
        if (!sink.Add(type))
            return;
        foreach (var nested in type.Types)
            AddWithNested(nested, sink);
    }

    /// <summary>
    /// Both the short and module-qualified names of every withdrawn LOCAL type, unioned with the
    /// module-qualified <paramref name="externalNames"/> of dependency-quarantined types. The external names
    /// are added only in their supplied (module-qualified) form — never a synthesized short name — so a
    /// cross-module poison name can never false-match a same-named local type through the short-name path.
    /// </summary>
    private static HashSet<string> BuildNameSet(IEnumerable<TypeDecl> types, IReadOnlySet<string> externalNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!string.IsNullOrEmpty(type.Name))
                names.Add(type.Name);
            var qualified = type.SwiftTypeName?.ModuleQualifiedName;
            if (!string.IsNullOrEmpty(qualified))
                names.Add(qualified!);
        }
        foreach (var external in externalNames)
            names.Add(external);
        return names;
    }

    /// <summary>
    /// Whether a type's inheritance, conformance, or stored-field layout reaches a withdrawn type — the
    /// structural edges that make the type itself indeterminate. Deliberately excludes the type's own
    /// generic where-clause constraints, which the proof owns (see class remarks).
    /// </summary>
    private static bool StructurallyReaches(
        TypeDecl type,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName)
    {
        // Superclass chain (Swift class parents are carried by name).
        if (type is ClassDecl classDecl)
        {
            foreach (var superclassName in classDecl.SuperclassNames)
            {
                if (withdrawnNames.Contains(superclassName))
                    return true;
            }
        }

        // Protocol inheritance.
        if (type is ProtocolDecl protocolDecl)
        {
            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                if (InternalTypeReferenceWalker.Reaches(inherited, withdrawnNames, currentModuleName))
                    return true;
            }
        }

        // Nominal-type conformances.
        foreach (var conformance in ConformancesOf(type))
        {
            var protocol = conformance.Protocol;
            if (protocol is null)
                continue;
            if (withdrawnNames.Contains(protocol.Name) ||
                withdrawnNames.Contains(protocol.ModuleQualifiedName))
                return true;
        }

        // Stored fields: a stored property whose declared type embeds a withdrawn type contaminates the
        // layout. A computed property does not — it is a signature edge, withdrawn as a leaf in 3b.
        foreach (var property in type.Properties)
        {
            if (!property.HasStorage)
                continue;
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(property, withdrawnNames, currentModuleName))
                return true;
        }

        // Enum associated-value payloads: a case payload whose type embeds a withdrawn type makes the
        // enum's storage indeterminate exactly as a stored struct field does — the payload IS the enum's
        // in-line layout for that case. Withdraw the enum whole (its cases can't be individually dropped
        // without changing the type's ABI), mirroring the stored-field policy above.
        if (type is EnumDecl enumDecl)
        {
            foreach (var enumCase in enumDecl.Cases)
            {
                foreach (var associated in enumCase.AssociatedValues)
                {
                    if (InternalTypeReferenceWalker.Reaches(associated, withdrawnNames, currentModuleName))
                        return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<TypeConformance> ConformancesOf(TypeDecl type) => type switch
    {
        StructDecl sd => sd.Conformances,
        ClassDecl cd => cd.Conformances,
        EnumDecl ed => ed.Conformances,
        _ => Enumerable.Empty<TypeConformance>(),
    };

    private static void CollectSignatureWithdrawals(
        TypeDecl owner,
        TypeDecl scanScope,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName,
        HashSet<RecoveryUnitId> withdrawals,
        ILogger logger)
    {
        foreach (var method in scanScope.Methods)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(method, withdrawnNames, currentModuleName))
                AddLeaf(method, RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
        foreach (var property in scanScope.Properties)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(property, withdrawnNames, currentModuleName))
                AddLeaf(property, RecoveryUnitId.ForAccessorGroup(DeclIdFactory.ForProperty(property)),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
        foreach (var subscript in scanScope.Subscripts)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(subscript, withdrawnNames, currentModuleName))
                AddLeaf(subscript, RecoveryUnitId.ForAccessorGroup(DeclIdFactory.ForSubscript(subscript)),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
        // Operators carry a real signature through their underlying method (an operand or result can name
        // a withdrawn type). The operator emitter gates on DeclIdFactory.ForOperator against the same poison
        // list, so an operator withdrawn here is genuinely skipped — leaf-withdrawable like a method.
        foreach (var op in scanScope.Operators)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(op.UnderlyingMethod, withdrawnNames, currentModuleName))
                AddLeaf(op, RecoveryUnitId.Create(DeclIdFactory.ForOperator(op), RecoveryScope.LeafApi),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
    }

    private static void CollectModuleLevelWithdrawals(
        ModuleDecl moduleDecl,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName,
        HashSet<RecoveryUnitId> withdrawals,
        ILogger logger)
    {
        foreach (var method in moduleDecl.Methods)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(method, withdrawnNames, currentModuleName))
                AddLeaf(method, RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
        foreach (var property in moduleDecl.Properties)
        {
            if (InternalTypeReferenceWalker.SignatureReachesInternalType(property, withdrawnNames, currentModuleName))
                AddLeaf(property, RecoveryUnitId.ForAccessorGroup(DeclIdFactory.ForProperty(property)),
                    withdrawnNames, currentModuleName, withdrawals, logger);
        }
    }

    private static void AddLeaf(
        BaseDecl decl,
        RecoveryUnitId unit,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName,
        HashSet<RecoveryUnitId> withdrawals,
        ILogger logger)
    {
        if (withdrawals.Add(unit))
            RecordWithdrawal(decl, unit, WithdrawalReason.SignatureEdge, withdrawnNames, currentModuleName, logger);
    }

    /// <summary>
    /// Re-scans the retained surface for the one reference channel withdrawal does not model: a retained
    /// type's own generic where-clause constraint on a withdrawn type. After the structural fixpoint no
    /// retained type reaches a withdrawn type by inheritance, conformance, or stored field, and 3b
    /// withdrew every retained member/free signature edge, so those channels need no re-check — but this
    /// scan looks for the where-clause residual ONLY. A null return means that channel is clear, not that
    /// the closure is complete against channels this plane never enumerated; the emission planes that can
    /// resolve a withdrawn type independently are kept sound by being handed the withdrawal set, not by
    /// this scan. Returns the reason to fail closed, or null when the modelled residual channel is clear.
    /// </summary>
    private static string? FindUnprovenResidual(
        IEnumerable<TypeDecl> allTypes,
        HashSet<TypeDecl> withdrawnTypes,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName)
    {
        foreach (var type in allTypes)
        {
            if (withdrawnTypes.Contains(type))
                continue;
            foreach (var generic in type.GenericParameters)
            {
                if (GenericConstraintReaches(generic, withdrawnNames, currentModuleName))
                {
                    return $"retained type '{type.SwiftTypeName?.ModuleQualifiedName ?? type.Name}' has a " +
                        "generic where-clause constraint on a type quarantined at ingestion; withdrawing it " +
                        "as a leaf is impossible and its whole-type withdrawal needs generic-context closure " +
                        "reasoning this plane does not own, so the withdrawal closure cannot be proven complete.";
                }
            }
        }
        return null;
    }

    private static bool GenericConstraintReaches(
        GenericArgumentDecl generic,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName)
    {
        foreach (var conformance in generic.GenericConformances.Concat(generic.AssosiatedTypeConformances))
        {
            var target = conformance.ConformanceTarget;
            if (target is null)
                continue;
            var qualified = target.ToString();
            if (!string.IsNullOrEmpty(qualified) && withdrawnNames.Contains(qualified))
                return true;
            if (string.Equals(target.Module, currentModuleName, StringComparison.Ordinal) &&
                withdrawnNames.Contains(target.Name))
                return true;
        }
        return false;
    }

    private enum WithdrawalReason
    {
        QuarantinedTypeOrClosure,
        SignatureEdge,
    }

    private static void RecordWithdrawal(
        BaseDecl decl,
        RecoveryUnitId unit,
        WithdrawalReason reason,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName,
        ILogger logger)
    {
        // The quarantined type itself is already ledgered by the parser (Cause=MalformedTypeRecord,
        // Status=Quarantined). Every OTHER unit here is a dependent withdrawn to keep the closure sound;
        // its ledger row names the malformed root it depends on so a reader can trace the withdrawal back.
        var isQuarantinedRoot = decl is TypeDecl { IsIngestionQuarantined: true };
        if (isQuarantinedRoot)
            return; // Do not double-ledger the root; the parser already recorded it.

        var input = LedgerIdentityOf(decl, currentModuleName);
        if (input is null)
            return; // Only a null or module decl yields no identity; neither is ever withdrawn here.

        var referenced = FirstReachedWithdrawnName(decl, withdrawnNames, currentModuleName);
        var evidence = reason == WithdrawalReason.QuarantinedTypeOrClosure
            ? $"withdrawn whole: its layout/identity embeds quarantined type '{referenced ?? "?"}' " +
              "(inheritance, conformance, stored field, or enum payload)"
            : $"withdrawn as a leaf: its signature reaches quarantined type '{referenced ?? "?"}'";

        InputResolutionReport.RecordLedgerEntry(new IngestionLedgerEntry(
            Input: input,
            Parent: LedgerIdentityOf(decl.ParentDecl, currentModuleName),
            Plane: IngestionPlane.Degrade,
            Cause: IngestionCause.MalformedTypeRecord,
            Referenced: referenced,
            Disposition: IngestionDisposition.QuarantineType,
            ClosureEvidence: evidence,
            Status: IngestionStatus.Quarantined));

        // Warning, not information: a withdrawal silently removes public API a consumer asked for.
        // At information level it is invisible in a default-verbosity build, so the consumer's only
        // signal is a compile error against a member that used to exist — the loss has to be
        // attributable at the same level as the quarantine that caused it.
        logger.LogWarning(
            "SWIFTBIND046: withdrawing {Unit} — {Evidence}.",
            unit.Describe(), evidence);
    }

    private static string? FirstReachedWithdrawnName(
        BaseDecl decl,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName)
    {
        // Report the specific withdrawn type this declaration touches: a structural edge for a
        // whole-type withdrawal, or the first signature-reachable withdrawn type for a leaf.
        switch (decl)
        {
            case TypeDecl type when TypeReachesName(type, withdrawnNames, currentModuleName) is { } structural:
                return structural;
            case OperatorDecl op:
                foreach (var arg in op.UnderlyingMethod.CSSignature)
                {
                    if (FirstWithdrawnNameInSpec(arg.SwiftTypeSpec, withdrawnNames) is { } n)
                        return n;
                }
                return null;
            case MethodDecl method:
                foreach (var arg in method.CSSignature)
                {
                    if (FirstWithdrawnNameInSpec(arg.SwiftTypeSpec, withdrawnNames) is { } n)
                        return n;
                }
                return null;
            case PropertyDecl property:
                return FirstWithdrawnNameInSpec(property.SwiftTypeSpec, withdrawnNames);
            case SubscriptDecl subscript:
                if (FirstWithdrawnNameInSpec(subscript.ReturnTypeSpec, withdrawnNames) is { } r)
                    return r;
                foreach (var index in subscript.IndexParameters)
                {
                    if (FirstWithdrawnNameInSpec(index.SwiftTypeSpec, withdrawnNames) is { } n)
                        return n;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>First withdrawn type name reachable anywhere in a type spec, or null. Report-only.</summary>
    private static string? FirstWithdrawnNameInSpec(TypeSpec? spec, IReadOnlySet<string> withdrawnNames)
    {
        switch (spec)
        {
            case null:
                return null;
            case NamedTypeSpec named:
                if (withdrawnNames.Contains(named.Name))
                    return named.Name;
                if (!string.IsNullOrEmpty(named.NameWithoutModule) && withdrawnNames.Contains(named.NameWithoutModule))
                    return named.NameWithoutModule;
                foreach (var generic in named.GenericParameters)
                {
                    if (FirstWithdrawnNameInSpec(generic, withdrawnNames) is { } n)
                        return n;
                }
                return named.InnerType is { } inner ? FirstWithdrawnNameInSpec(inner, withdrawnNames) : null;
            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                {
                    if (FirstWithdrawnNameInSpec(element, withdrawnNames) is { } n)
                        return n;
                }
                return null;
            case ClosureTypeSpec closure:
                return FirstWithdrawnNameInSpec(closure.Arguments, withdrawnNames)
                    ?? FirstWithdrawnNameInSpec(closure.ReturnType, withdrawnNames);
            case ProtocolListTypeSpec protocolList:
                foreach (var protocol in protocolList.Protocols.Keys)
                {
                    if (FirstWithdrawnNameInSpec(protocol, withdrawnNames) is { } n)
                        return n;
                }
                return null;
            default:
                return null;
        }
    }

    private static string? TypeReachesName(
        TypeDecl type,
        IReadOnlySet<string> withdrawnNames,
        string currentModuleName)
    {
        if (type is ClassDecl classDecl)
        {
            foreach (var superclassName in classDecl.SuperclassNames)
            {
                if (withdrawnNames.Contains(superclassName))
                    return superclassName;
            }
        }
        // Protocol inheritance edge — mirrors the StructurallyReaches protocol-inheritance branch so a
        // protocol withdrawn for inheriting a (possibly cross-module) quarantined protocol reports the
        // specific parent it reaches, rather than an anonymous '?'.
        if (type is ProtocolDecl protocolDecl)
        {
            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                if (FirstWithdrawnNameInSpec(inherited, withdrawnNames) is { } inheritedName)
                    return inheritedName;
            }
        }
        foreach (var conformance in ConformancesOf(type))
        {
            var protocol = conformance.Protocol;
            if (protocol is null)
                continue;
            if (withdrawnNames.Contains(protocol.ModuleQualifiedName))
                return protocol.ModuleQualifiedName;
            if (withdrawnNames.Contains(protocol.Name))
                return protocol.Name;
        }
        // Stored-field layout edge — mirrors the StructurallyReaches stored-property branch so a struct or
        // class withdrawn because a stored field's declared type embeds a withdrawn type reports the
        // specific type it reaches, rather than an anonymous '?'. A computed property is a signature edge
        // (withdrawn as a leaf), not a layout edge, so only stored properties count here.
        foreach (var property in type.Properties)
        {
            if (!property.HasStorage)
                continue;
            if (FirstWithdrawnNameInSpec(property.SwiftTypeSpec, withdrawnNames) is { } fieldName)
                return fieldName;
        }
        if (type is EnumDecl enumDecl)
        {
            foreach (var enumCase in enumDecl.Cases)
            {
                foreach (var associated in enumCase.AssociatedValues)
                {
                    if (FirstWithdrawnNameInSpec(associated, withdrawnNames) is { } payload)
                        return payload;
                }
            }
        }
        return null;
    }

    private static IngestionInputIdentity? LedgerIdentityOf(BaseDecl? decl, string currentModuleName)
    {
        if (decl is null)
            return null;
        if (decl is ModuleDecl)
            return null;

        var symbol = decl switch
        {
            MethodDecl m when !string.IsNullOrEmpty(m.MangledName) => m.MangledName,
            OperatorDecl o when !string.IsNullOrEmpty(o.UnderlyingMethod.MangledName) => o.UnderlyingMethod.MangledName,
            SubscriptDecl s when !string.IsNullOrEmpty(s.MangledName) => s.MangledName,
            TypeDecl t when !string.IsNullOrEmpty(t.MangledName) => t.MangledName,
            _ => IngestionInputIdentity.AbsentSymbol,
        };
        var module = decl.ModuleDecl?.Name ?? currentModuleName;
        var kind = decl.GetType().Name.Replace("Decl", string.Empty);
        return new IngestionInputIdentity(module, kind, symbol);
    }
}

/// <summary>
/// The outcome of an ingestion-quarantine closure walk: the units to seed emission's poison list with,
/// whether the withdrawal closure is provably complete, and the reason it is not when it isn't.
/// </summary>
/// <param name="Withdrawals">
/// The recovery units to seed the FIRST render with, each origin-tagged
/// <see cref="EmitterFaultOrigin.IngestionWithdrawal"/> at the seam. Empty when no type was quarantined.
/// </param>
/// <param name="ProvenComplete">
/// True when every retained declaration that reaches a withdrawn type was itself withdrawn. False means
/// a residual reference remains and the module must fail closed (SWIFTBIND120).
/// </param>
/// <param name="UnprovenReason">Human-readable reason the closure is not provable; null when it is.</param>
/// <param name="WithdrawnTypeNames">
/// The module-qualified Swift names of the LOCAL types withdrawn whole. The poison list is the
/// authority for every plane that runs inside the emission attempt; this name set exists for the
/// planes that run OUTSIDE it (the Swift type-ownership manifest, written after emission returns),
/// which cannot read the ambient attempt and must be told explicitly.
/// </param>
internal sealed record IngestionQuarantineResult(
    IReadOnlySet<RecoveryUnitId> Withdrawals,
    bool ProvenComplete,
    string? UnprovenReason,
    IReadOnlySet<string>? WithdrawnTypeNames = null)
{
    /// <summary>The result for a module with no ingestion quarantines — nothing withdrawn, trivially proven.</summary>
    public static IngestionQuarantineResult Empty { get; } =
        new(new HashSet<RecoveryUnitId>(), ProvenComplete: true, UnprovenReason: null);
}
