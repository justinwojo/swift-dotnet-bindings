// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// One method-own generic parameter that the opening wrapper re-enters by casting its
/// runtime metadata to an existential metatype.
/// </summary>
/// <param name="SwiftName">The parser's name for the parameter (the key into
/// <see cref="MethodEnvironment.GenericTypeMapping"/> and the parameter's position in
/// <see cref="MethodDecl.GenericParameters"/>).</param>
/// <param name="Ordinal">Zero-based position in <see cref="MethodDecl.GenericParameters"/>,
/// which is also the position of the matching metadata pointer in the @_cdecl signature.</param>
/// <param name="CSharpName">The emitted C# type-parameter name (e.g. <c>T0</c>), used in the
/// managed refusal message.</param>
/// <param name="ConstraintTargets">Module-qualified Swift protocol names taken verbatim from the
/// method's generic signature. Empty means the parameter is unconstrained.</param>
internal sealed record MlgOpenedGeneric(
    string SwiftName,
    int Ordinal,
    string CSharpName,
    IReadOnlyList<string> ConstraintTargets)
{
    /// <summary>The runtime proof used to open this parameter.</summary>
    public MlgOpeningStrategy Strategy { get; init; } = MlgOpeningStrategy.Existential;

    /// <summary>
    /// The one-level associated-type conformance proved by a conditional carrier. Null on the
    /// ordinary and direct-superclass routes.
    /// </summary>
    public GenericRequirement? ConditionalRequirement { get; init; }

    /// <summary>The direct Swift superclass adopted by the wrapper-private carrier protocol.</summary>
    public string? SuperclassTarget { get; init; }

    /// <summary>
    /// Every requirement on this root in parser order. ConstraintTargets is the normalized Swift
    /// spelling used by the opening cast; this list remains faithful for refusal diagnostics.
    /// </summary>
    public IReadOnlyList<string> DiagnosticRequirements { get; init; } = Array.Empty<string>();

    /// <summary>The Swift constraint clause for a local generic function, or "" when unconstrained.</summary>
    public string ConstraintClause =>
        ConstraintTargets.Count == 0 ? "" : ": " + string.Join(" & ", ConstraintTargets);

    /// <summary>The existential metatype the runtime metadata is cast to before opening.</summary>
    public string ExistentialMetatype =>
        ConstraintTargets.Count == 1
            ? $"any {ConstraintTargets[0]}.Type"
            : $"any ({string.Join(" & ", ConstraintTargets)}).Type";
}

/// <summary>The bounded runtime proof mechanisms admitted by the method-generic opening route.</summary>
internal enum MlgOpeningStrategy
{
    /// <summary>Ordinary protocol existential cast, composition, or unconstrained opening.</summary>
    Existential,

    /// <summary>Exact primary-associated-type cast, currently Collection&lt;Swift.String&gt; only.</summary>
    ParameterizedExistential,

    /// <summary>A wrapper-private generic carrier with one conditional Element conformance.</summary>
    AssociatedTypeCarrier,

    /// <summary>A wrapper-private protocol adopted by one public Swift superclass.</summary>
    SuperclassCarrier,
}

/// <summary>
/// The shape analysis behind the method-level-generic opening route: which members can be
/// rerouted off the direct <c>CallConvSwift</c> P/Invoke and onto a free <c>@_cdecl</c> wrapper
/// that takes the method's own type-argument metadata as ordinary pointers.
///
/// <para>
/// A <c>@_cdecl</c> function cannot carry generic context, so the only C-convention route out of
/// one is a free function at module scope that re-enters the generic context by opening the
/// metadata (SE-0352 implicit existential opening into a local generic function). That is what
/// removes the untyped <c>SwiftSelf</c> argument — and with it the Mono full-AOT
/// <c>x20</c> GC-safe-region clobber — from these members.
/// </para>
///
/// <para>
/// The cast is the authoritative conformance check and it runs BEFORE the payload pointer is
/// dereferenced, so a type argument whose Swift metadata does not conform is refused rather than
/// reinterpreted. Refusal travels its own <see cref="CdeclPhase.OpenRefusal"/> out-parameter
/// (ordinal-encoded: 0 = success, N = the N-th generic parameter refused) so it stays distinct
/// from both the C return channel and the <c>errorOut</c> channel, which remains keyed strictly
/// on <see cref="MethodDecl.Throws"/>.
/// </para>
/// </summary>
internal static class MethodLevelGenericOpening
{
    /// <summary>
    /// Targets that are not a castable protocol existential, over and above the stdlib marker
    /// protocols. A marker protocol leaves no runtime conformance record, so <c>any Marker.Type</c>
    /// is rejected by swiftc ("marker protocol cannot be used in a cast") and a member constrained
    /// on one cannot be opened — but the markers themselves are recognised by the generator's one
    /// marker oracle, <see cref="WrapperEmitterHelpers.IsStdlibMarkerProtocol"/>, so a NEW marker
    /// belongs there and not in this set. The names kept here are the non-protocol targets
    /// (<c>Any</c>, <c>AnyObject</c>, <c>AnyActor</c>) plus the simple-name spellings the marker
    /// names have carried on this route since it was written, which keep a same-named target from
    /// a user module conservatively declined.
    /// </summary>
    private static readonly HashSet<string> MarkerOrNonCastableTargets = new(StringComparer.Ordinal)
    {
        "Sendable", "Copyable", "Escapable", "BitwiseCopyable", "AnyObject", "Any", "AnyActor",
    };

    /// <summary>
    /// True when this member's own generic parameters can be reconstructed by opening their
    /// runtime metadata. Pure — safe to call from the wrapper-eligibility traversal before any
    /// promotion flag is set.
    /// </summary>
    internal static bool IsOpenable(MethodEnvironment env) => TryBuildPlan(env, out _);

    /// <summary>
    /// True when the opening wrapper is the route this environment actually emits on — the
    /// promotion flag is set AND the shape still builds a plan.
    ///
    /// <para>
    /// Both halves are load-bearing. <see cref="MethodDecl"/> is a record, and several emitters
    /// specialize a member by cloning it (<c>method with { GenericParameters = [], … }</c>) — the
    /// closed concrete-specialization overloads, the trimmed default-parameter overloads. A clone
    /// inherits <see cref="MethodDecl.UsesMethodLevelGenericOpening"/> from its generic original
    /// while no longer having any own generic parameters to open, so a site that reads the raw flag
    /// would give the clone an <see cref="CdeclPhase.OpenRefusal"/> parameter its wrapper never
    /// emits and suppress the witness tables its wrapper still needs. Asking for the plan as well
    /// puts every consumer on the same answer the wrapper emitter itself reaches.
    /// </para>
    /// </summary>
    internal static bool AppliesTo(MethodEnvironment env)
        => env.MethodDecl.UsesMethodLevelGenericOpening && TryBuildPlan(env, out _);

    /// <summary>
    /// Builds the opening plan for a member, or returns false with the shape that stops it.
    /// The declines here leave the member on the direct route under the existing
    /// <c>method_level_generics</c> rejection reason — no new skip class, no new baseline key.
    /// </summary>
    internal static bool TryBuildPlan(MethodEnvironment env, out IReadOnlyList<MlgOpenedGeneric> opened)
    {
        opened = Array.Empty<MlgOpenedGeneric>();
        var methodDecl = env.MethodDecl;

        if (!WrapperValidation.HasMethodOwnGenericParameters(methodDecl))
            return false;

        // Constructors, accessors and async members have their own emission lanes; the opening
        // wrapper is the synchronous method lane only.
        if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
            return false;

        // A generic PARENT plus a method-own generic needs the parent metatype reconstructed as
        // well as the method's — a different, combined route. Guard 5b already declines these;
        // stating it here keeps the predicate answerable on its own.
        if (env.ParentDecl is TypeDecl { IsGeneric: true })
            return false;

        // The opened payload is read back with `.pointee`, which copies. A ~Copyable payload (or
        // parent) cannot take that, and a variadic/closure parameter has no metadata pointer.
        if (methodDecl.HasVariadicParameter)
            return false;
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
            return false;
        if (methodDecl.CSSignature.Any(a =>
                WrapperValidation.IsNonCopyableType(a.SwiftTypeSpec, env.TypeDatabase, methodDecl.ModuleDecl)))
            return false;
        if (methodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
            return false;

        // The opening wrapper's ordinary argument mapper is a by-value transport. Even when an
        // inout argument does not mention the method's own generic parameter, routing it through
        // CdeclParamMapper.Map would erase the caller-visible write-back contract. Keep the B1
        // route deliberately narrower until it has an explicit inout carrier on both sides.
        if (methodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return false;

        var parentTypeParamNames = env.ParentDecl is TypeDecl { IsGeneric: true } parentTypeDecl
            ? new HashSet<string>(parentTypeDecl.GenericParameters.Select(p => p.TypeName), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var ownNames = new HashSet<string>(
            methodDecl.GenericParameters
                .Select(p => p.TypeName)
                .Where(n => !parentTypeParamNames.Contains(n)),
            StringComparer.Ordinal);

        // One metadata pointer per opened parameter, one nesting level per pointer. Three is the
        // widest shape in the corpus; beyond that the nesting is untested rather than unsound.
        if (ownNames.Count == 0 || ownNames.Count > 3)
            return false;

        // Return position: v1 keeps the return on shapes the managed side already proves. A return
        // that MENTIONS an own generic parameter needs the indirect-result buffer sized from the
        // opened layout, which is a separate managed change.
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        if (TypeSpecMentionsAny(returnSpec, ownNames))
            return false;

        // A dynamic `Self` return has to be written as the parent type inside the opened bodies — a
        // local generic function has no enclosing type for `Self` to resolve against. The renderer
        // owns which shapes it can spell; one it cannot is declined here so the member keeps its
        // working direct route instead of emitting a wrapper the Swift compile then withdraws.
        if (returnSpec.HasDynamicSelf
            && MethodLevelGenericWrapperEmitter.TryRenderDynamicSelfReturn(env.ParentDecl, returnSpec) == null)
            return false;

        // Parameter positions: this route has already established that every argument is by value,
        // and an own generic parameter may appear only as a whole payload. A composite
        // (`Pair<A, B>`) cannot bind a typed pointer from the opened type without a second layout
        // derivation.
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (!TypeSpecMentionsAny(arg.SwiftTypeSpec, ownNames))
                continue;
            if (arg.SwiftTypeSpec is not NamedTypeSpec named
                || named.GenericParameters.Count > 0
                || !ownNames.Contains(named.Name))
                return false;
        }

        var sig = methodDecl.ParsedGenericSignature;
        var plan = new List<MlgOpenedGeneric>();
        for (int i = 0; i < methodDecl.GenericParameters.Count; i++)
        {
            var name = methodDecl.GenericParameters[i].TypeName;
            if (!ownNames.Contains(name))
                continue;

            var csName = env.GenericTypeMapping.TryGetValue(name, out var mapped)
                ? mapped.TypeParameter
                : name;
            var requirements = sig.Requirements
                .Where(r => string.Equals(r.SubjectRoot, name, StringComparison.Ordinal))
                .ToList();

            if (!TryBuildOpenedGeneric(
                    name, i, csName, requirements, ownNames.Count, env.TypeDatabase, out var openedGeneric))
                return false;

            plan.Add(openedGeneric);
        }

        if (plan.Count == 0)
            return false;

        opened = plan;
        return true;
    }

    private static bool TryBuildOpenedGeneric(
        string swiftName,
        int ordinal,
        string csharpName,
        IReadOnlyList<GenericRequirement> requirements,
        int ownGenericCount,
        ITypeDatabase typeDatabase,
        out MlgOpenedGeneric opened)
    {
        opened = null!;
        var diagnostics = requirements.Select(RenderRequirement).ToList();

        if (requirements.Count == 0)
        {
            opened = new MlgOpenedGeneric(swiftName, ordinal, csharpName, Array.Empty<string>())
            {
                DiagnosticRequirements = diagnostics,
            };
            return true;
        }

        if (TryBuildCollectionStringTargets(requirements, typeDatabase, out var collectionTargets))
        {
            opened = new MlgOpenedGeneric(swiftName, ordinal, csharpName, collectionTargets)
            {
                Strategy = MlgOpeningStrategy.ParameterizedExistential,
                DiagnosticRequirements = diagnostics,
            };
            return true;
        }

        // Conditional carrier scope is deliberately one-root in B1. A relationship involving
        // another root belongs to the dependent-constraint work; independently opening the roots
        // would not prove the relationship between them.
        if (ownGenericCount == 1
            && TryBuildAssociatedCarrier(requirements, typeDatabase, out var rootTargets, out var conditional))
        {
            opened = new MlgOpenedGeneric(swiftName, ordinal, csharpName, rootTargets)
            {
                Strategy = MlgOpeningStrategy.AssociatedTypeCarrier,
                ConditionalRequirement = conditional,
                DiagnosticRequirements = diagnostics,
            };
            return true;
        }

        // One direct Swift superclass only. Mixed superclass/protocol bounds, generic targets and
        // ObjC-rooted classes remain probe-first rather than being silently weakened.
        if (ownGenericCount == 1
            && requirements.Count == 1
            && requirements[0] is { IsDirect: true, Kind: GenericRequirementKind.Conformance } classReq
            && TryResolveSwiftSuperclass(classReq.Target, typeDatabase))
        {
            opened = new MlgOpenedGeneric(swiftName, ordinal, csharpName, new[] { classReq.Target })
            {
                Strategy = MlgOpeningStrategy.SuperclassCarrier,
                SuperclassTarget = classReq.Target,
                DiagnosticRequirements = diagnostics,
            };
            return true;
        }

        var targets = new List<string>();
        foreach (var requirement in requirements)
        {
            if (!requirement.IsDirect || requirement.Kind != GenericRequirementKind.Conformance)
                return false;
            if (!IsPlainCastableProtocol(requirement.Target, typeDatabase))
                return false;
            if (!targets.Contains(requirement.Target, StringComparer.Ordinal))
                targets.Add(requirement.Target);
        }

        // Preserve the existing byte-stable composition order on the v1 route.
        targets.Sort(StringComparer.Ordinal);
        opened = new MlgOpenedGeneric(swiftName, ordinal, csharpName, targets)
        {
            DiagnosticRequirements = diagnostics,
        };
        return true;
    }

    private static bool TryBuildCollectionStringTargets(
        IReadOnlyList<GenericRequirement> requirements,
        ITypeDatabase typeDatabase,
        out IReadOnlyList<string> targets)
    {
        targets = Array.Empty<string>();
        var consumed = new HashSet<GenericRequirement>();

        var constructed = requirements
            .Where(r => r.IsDirect
                && r.Kind == GenericRequirementKind.Conformance
                && IsExactCollectionString(r.Target))
            .ToList();
        if (constructed.Count == 1)
        {
            consumed.Add(constructed[0]);

            // Swift interfaces can redundantly preserve the primary-associated-type spelling and
            // its desugared same-type proof. They express one constraint, so consume both while
            // leaving every unrelated direct protocol requirement available for composition.
            var redundantElementString = requirements
                .Where(r => !r.IsDirect
                    && r.Kind == GenericRequirementKind.SameType
                    && string.Equals(r.MemberPath, "Element", StringComparison.Ordinal)
                    && IsSwiftString(r.Target))
                .ToList();
            if (redundantElementString.Count > 1)
                return false;
            if (redundantElementString.Count == 1)
                consumed.Add(redundantElementString[0]);
        }
        else if (constructed.Count > 1)
        {
            return false;
        }
        else
        {
            var collection = requirements
                .Where(r => r.IsDirect
                    && r.Kind == GenericRequirementKind.Conformance
                    && IsPlainCollection(r.Target))
                .ToList();
            var elementString = requirements
                .Where(r => !r.IsDirect
                    && r.Kind == GenericRequirementKind.SameType
                    && string.Equals(r.MemberPath, "Element", StringComparison.Ordinal)
                    && IsSwiftString(r.Target))
                .ToList();
            if (collection.Count != 1 || elementString.Count != 1)
                return false;
            consumed.Add(collection[0]);
            consumed.Add(elementString[0]);
        }

        var additional = new List<string>();
        foreach (var requirement in requirements)
        {
            if (consumed.Contains(requirement))
                continue;
            if (!requirement.IsDirect
                || requirement.Kind != GenericRequirementKind.Conformance
                || !IsPlainCastableProtocol(requirement.Target, typeDatabase))
                return false;
            additional.Add(requirement.Target);
        }

        additional.Sort(StringComparer.Ordinal);
        targets = new[] { "Swift.Collection<Swift.String>" }.Concat(additional).ToList();
        return true;
    }

    private static bool TryBuildAssociatedCarrier(
        IReadOnlyList<GenericRequirement> requirements,
        ITypeDatabase typeDatabase,
        out IReadOnlyList<string> rootTargets,
        out GenericRequirement conditional)
    {
        rootTargets = Array.Empty<string>();
        conditional = null!;

        var memberRequirements = requirements.Where(r => !r.IsDirect).ToList();
        if (memberRequirements.Count != 1
            || memberRequirements[0] is not { Kind: GenericRequirementKind.Conformance } member
            || !string.Equals(member.MemberPath, "Element", StringComparison.Ordinal)
            || !TryResolveCarrierTarget(member.Target, typeDatabase))
            return false;

        var directTargets = new List<string>();
        foreach (var requirement in requirements.Where(r => r.IsDirect))
        {
            if (requirement.Kind != GenericRequirementKind.Conformance
                || !IsPlainCastableProtocol(requirement.Target, typeDatabase))
                return false;
            directTargets.Add(requirement.Target);
        }
        if (directTargets.Count == 0)
            return false;

        directTargets = directTargets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        rootTargets = directTargets;
        conditional = member;
        return true;
    }

    private static bool TryResolveCarrierTarget(string target, ITypeDatabase typeDatabase)
    {
        if (!TryGetTypeRecord(target, typeDatabase, out var record))
            return false;
        if (record.Kind == TypeRecordKind.Protocol)
            return !IsMarkerOrNonCastable(target);
        if (record.Kind == TypeRecordKind.Class)
            return !record.Flags.HasFlag(TypeRecordFlags.ObjCRooted)
                && !record.Flags.HasFlag(TypeRecordFlags.ObjCBridged);
        return false;
    }

    private static bool TryResolveSwiftSuperclass(string target, ITypeDatabase typeDatabase)
    {
        if (target.Contains('<') || !TryGetTypeRecord(target, typeDatabase, out var record))
            return false;
        return record.Kind == TypeRecordKind.Class
            && !record.Flags.HasFlag(TypeRecordFlags.ObjCRooted)
            && !record.Flags.HasFlag(TypeRecordFlags.ObjCBridged);
    }

    private static bool TryGetTypeRecord(string target, ITypeDatabase typeDatabase, out TypeRecord record)
    {
        record = null!;
        if (!SwiftTypeName.TryFromModuleQualifiedName(target, out var typeName)
            || typeName == null
            || !typeDatabase.TryGetTypeRecord(typeName, out var resolved)
            || resolved == null)
            return false;
        record = resolved;
        return true;
    }

    private static bool IsPlainCastableProtocol(string target, ITypeDatabase typeDatabase)
        => !target.Contains('<') && !IsMarkerOrNonCastable(target) && IsCastableProtocol(target, typeDatabase);

    private static bool IsMarkerOrNonCastable(string target)
    {
        var simple = target.Contains('.') ? target[(target.LastIndexOf('.') + 1)..] : target;
        return MarkerOrNonCastableTargets.Contains(simple)
            || WrapperEmitterHelpers.IsStdlibMarkerProtocol(target)
            || WrapperEmitterHelpers.IsStdlibMarkerProtocol(simple);
    }

    private static bool IsPlainCollection(string target)
        => string.Equals(target, "Collection", StringComparison.Ordinal)
            || string.Equals(target, "Swift.Collection", StringComparison.Ordinal);

    private static bool IsSwiftString(string target)
        => string.Equals(target, "String", StringComparison.Ordinal)
            || string.Equals(target, "Swift.String", StringComparison.Ordinal);

    private static bool IsExactCollectionString(string target)
        => string.Equals(target, "Collection<String>", StringComparison.Ordinal)
            || string.Equals(target, "Collection<Swift.String>", StringComparison.Ordinal)
            || string.Equals(target, "Swift.Collection<String>", StringComparison.Ordinal)
            || string.Equals(target, "Swift.Collection<Swift.String>", StringComparison.Ordinal);

    private static string RenderRequirement(GenericRequirement requirement)
        => $"{string.Join('.', requirement.Subject)} "
            + (requirement.Kind == GenericRequirementKind.Conformance ? ":" : "==")
            + $" {requirement.Target}";

    /// <summary>
    /// The name the refusal out-parameter actually carries in the emitted P/Invoke. The slot is
    /// added under <see cref="MethodLevelGenericWrapperEmitter.RefusalParameterName"/>, but a user
    /// parameter is free to be spelled the same way, and the signature builder deduplicates by
    /// letting the FIRST occurrence keep the name and suffixing the later one — which is always
    /// this slot, since the contract places OpenRefusal after the arguments. Reading the constant
    /// instead of the emitted name would bind the check to the user's argument: a caller-supplied
    /// value would be read as a refusal ordinal, and a real refusal would go unnoticed and let an
    /// uninitialized indirect-result buffer be marked live.
    /// </summary>
    internal static string ResolveRefusalParameterName(
        IReadOnlyList<Parameter> pInvokeParameters)
    {
        var constant = MethodLevelGenericWrapperEmitter.RefusalParameterName;
        for (int i = pInvokeParameters.Count - 1; i >= 0; i--)
        {
            var name = pInvokeParameters[i].Name;
            if (string.Equals(name, constant, StringComparison.Ordinal)
                || (name.StartsWith(constant + "_", StringComparison.Ordinal)
                    && name[(constant.Length + 1)..].All(char.IsDigit)))
            {
                return name;
            }
        }

        return constant;
    }

    /// <summary>
    /// Renders the managed refusal check emitted immediately after the P/Invoke: the wrapper
    /// reports a type argument it could not open through the
    /// <see cref="CdeclPhase.OpenRefusal"/> out-parameter, and the check turns that into a typed
    /// exception before any result is read. The value is the 1-based ordinal of the generic
    /// parameter that failed, so the message can name the parameter, its Swift constraints and the
    /// concrete type argument the caller supplied.
    ///
    /// <para>
    /// This runs BEFORE the indirect-result live marker, so a refused call leaves the result buffer
    /// marked uninitialized and the cleanup frees it without running a value-witness destroy over
    /// bytes Swift never wrote.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> BuildRefusalCheckLines(
        MethodEnvironment env, IReadOnlyList<MlgOpenedGeneric> opened, string refusalParameterName)
    {
        var refused = refusalParameterName;
        var memberName = env.CSharpMethodName;
        var lines = new List<string>
        {
            $"if ({refused} != 0)",
            $"    throw new global::Swift.Runtime.SwiftRuntimeException({refused} switch",
            "    {",
        };

        foreach (var og in opened)
        {
            var constraints = og.Strategy != MlgOpeningStrategy.Existential
                && og.DiagnosticRequirements.Count > 0
                ? "Swift requirement" + (og.DiagnosticRequirements.Count == 1 ? " " : "s ")
                  + string.Join(" & ", og.DiagnosticRequirements.Select(r => $"'{r}'"))
                : og.ConstraintTargets.Count == 0
                ? "the method's Swift constraints"
                : "Swift protocol" + (og.ConstraintTargets.Count == 1 ? " " : "s ")
                  + string.Join(" & ", og.ConstraintTargets.Select(t => $"'{t}'"));
            lines.Add($"        {og.Ordinal + 1} => $\"Type argument '{{typeof({og.CSharpName})}}' for '{og.CSharpName}' " +
                      $"does not conform to {constraints} at run time, so '{memberName}' cannot be called with it.\",");
        }

        lines.Add($"        _ => \"A type argument of '{memberName}' does not satisfy its Swift constraints at run time.\",");
        lines.Add("    });");
        return lines;
    }

    /// <summary>
    /// True when <paramref name="target"/> names a protocol the wrapper can write as
    /// <c>any {target}.Type</c>: a protocol known to the type database, or a Swift standard-library
    /// protocol the wrapper module can always see. A superclass bound resolves to a class record
    /// and is refused — a class bound is not a protocol, so the existential cast cannot express it.
    /// </summary>
    private static bool IsCastableProtocol(string target, ITypeDatabase typeDatabase)
    {
        // The target text comes from the library under binding, so use the non-throwing factory.
        if (!SwiftTypeName.TryFromModuleQualifiedName(target, out var typeName) || typeName == null)
            return false;

        if (typeDatabase.TryGetTypeRecord(typeName, out var record))
            return record.Kind == TypeRecordKind.Protocol;

        // Standard-library protocols (Swift.Sequence, Swift.Equatable, …) are not carried in the
        // module database but are always in scope for the wrapper.
        return string.Equals(typeName.Module, "Swift", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="spec"/> references any of <paramref name="names"/> anywhere in its
    /// structure (directly, or nested inside a generic argument, tuple element or closure).
    /// </summary>
    private static bool TypeSpecMentionsAny(TypeSpec spec, HashSet<string> names)
        => WrapperValidation.TypeSpecReferencesGenericParam(spec, names);
}
