// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>
/// Maps protocol constraints to known conforming types for concrete specialization.
/// When a Swift method has a generic parameter constrained to a protocol with associated types
/// or Self requirements (e.g., <c>func hash&lt;D: DataProtocol&gt;(data: D)</c>), C# cannot express
/// that constraint. This engine discovers concrete conformers and enables emitting one C# overload
/// per conformer.
///
/// Conformer sources:
/// 1. Module-local ABI — TypeConformance records on structs/classes/enums in the same module
/// 2. Specialization hints — manual protocol→conformer mappings in specialization-hints.json
///    for cross-module or standard library conformances
/// </summary>
public class ConcreteSpecializationEngine
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly Dictionary<string, List<ConcreteConformer>> _hintConformers;
    private readonly Dictionary<string, List<ConcreteConformer>> _abiConformers;
    private readonly HashSet<string> _abiIndexedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _abiDeclaredProtocolsByType =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProtocolDecl> _abiProtocols = new(StringComparer.Ordinal);
    private readonly string? _currentModuleName;
    private string? _indexedModuleName;
    private HashSet<string>? _indexedModuleDependencies;
    private readonly HashSet<CsmRejectedPairing> _rejectedPairings = new();

    /// <summary>
    /// Conformer pairings filtered out at the pairing step because the conformer fails
    /// a non-selected constraint on its generic parameter. Accumulates across all
    /// <see cref="FindSpecializableMethods"/> and <see cref="ResolveParentSpecializableParams"/>
    /// calls on this engine. Consumed by <see cref="EmissionReportEmitter.BuildReport"/>
    /// so audits and consumers can see which conformers were excluded and why.
    /// </summary>
    public IReadOnlyCollection<CsmRejectedPairing> RejectedPairings => _rejectedPairings;

    /// <summary>
    /// A concrete type that conforms to a protocol, usable for specialization.
    /// SwiftTypeName may be null for generic conformers (e.g., Array&lt;UInt8&gt;) that
    /// can't be represented as SwiftTypeName. Use SwiftQualifiedName for the full name.
    /// AssociatedTypes maps associated-type names (e.g., "Element") to their
    /// concrete Swift qualified type (e.g., "Swift.String"). Used to match conformers
    /// against same-type constraints like <c>T.Element == Swift.String</c>.
    /// </summary>
    public record ConcreteConformer(
        string SwiftQualifiedName,
        string CSharpType,
        SwiftTypeName? SwiftType = null,
        string? SwiftLiteral = null,
        IReadOnlyDictionary<string, string>? AssociatedTypes = null,
        IReadOnlyList<AvailabilityAnnotation>? AvailabilityAnnotations = null,
        IReadOnlyList<string>? AllowedModules = null);

    /// <summary>
    /// A method that can be specialized, along with its specialization info.
    /// </summary>
    public record SpecializableMethod(
        MethodDecl Method,
        List<SpecializableParam> SpecializableParams);

    /// <summary>
    /// A generic parameter that can be concretely specialized.
    /// <para>
    /// <see cref="CouplingConstraints"/> captures cross-param same-type constraints like
    /// <c>S.Element == T</c>. Each entry reads: "this param's conformer's
    /// <c>AssociatedTypes[AssocName]</c> must equal the chosen conformer of
    /// <c>OtherParamName</c>." These are applied at cartesian pairing time because the
    /// other param's chosen conformer isn't known at conformer-filter time.
    /// </para>
    /// <para>
    /// <see cref="IsParentGeneric"/> discriminates parent-type generic params (e.g. the
    /// <c>H</c> in <c>HMAC&lt;H&gt;.update&lt;D&gt;</c>) from method-own generic params.
    /// Parent entries appear at the leading positions of a pairing array and drive the
    /// concrete parent type name in Swift wrappers plus the closed-receiver type of the
    /// emitted C# extension method.
    /// </para>
    /// </summary>
    public record SpecializableParam(
        GenericArgumentDecl GenericParam,
        SwiftTypeName ConstraintProtocol,
        List<ConcreteConformer> Conformers,
        IReadOnlyList<(string AssocName, string OtherParamName)>? CouplingConstraints = null,
        bool IsParentGeneric = false);

    /// <summary>
    /// A conformer pairing that was rejected at the pairing step because the conformer
    /// fails to satisfy a non-selected protocol constraint on its generic parameter.
    /// <para>
    /// Background: <see cref="FindSpecializableProtocolConstraint"/> picks ONE protocol
    /// per generic param (the first PAT/Self requirement, else the first protocol with
    /// known conformers). When the param carries multiple constraints
    /// (e.g. <c>&lt;T where T : Decodable, T : MusicRecentlyPlayedRequestable&gt;</c>)
    /// the selected protocol's conformer set is necessarily a superset of the legal
    /// intersection. Without an intersection filter, the engine emits CSM overloads
    /// for conformers of the selected protocol that fail the other constraints — the
    /// generated Swift wrapper passes them into a `where` clause they don't satisfy
    /// (CS0311 on the C# side, "type 'X' does not conform to 'Y'" on the Swift side).
    /// </para>
    /// <para>
    /// Pairing rejections feed <c>binding-emission-report.json</c> so consumers and
    /// audits can see which conformers were filtered out and why. The collection is
    /// scoped to the engine instance (one per module).
    /// </para>
    /// </summary>
    public sealed record CsmRejectedPairing(
        string ParentType,
        string GenericParamName,
        string SelectedProtocol,
        string ConformerSwiftType,
        string MissingConstraint,
        string Reason);

    // --- JSON Model for specialization-hints.json ---

    private sealed class HintsFile
    {
        [JsonProperty("protocols")]
        public List<ProtocolHint> Protocols { get; set; } = new();
    }

    private sealed class ProtocolHint
    {
        [JsonProperty("protocol")]
        public string Protocol { get; set; } = string.Empty;

        [JsonProperty("conformers")]
        public List<ConformerHint> Conformers { get; set; } = new();
    }

    private sealed class ConformerHint
    {
        [JsonProperty("swiftType")]
        public string SwiftType { get; set; } = string.Empty;

        [JsonProperty("csharpType")]
        public string CSharpType { get; set; } = string.Empty;

        [JsonProperty("swiftLiteral")]
        public string? SwiftLiteral { get; set; }

        [JsonProperty("associatedTypes")]
        public Dictionary<string, string>? AssociatedTypes { get; set; }

        /// <summary>
        /// Optional allow-list of module names. When set, the conformer only applies
        /// while generating bindings for one of the listed modules. Null/empty means
        /// the conformer is global (current behavior for hints without a module tag).
        /// </summary>
        [JsonProperty("modules")]
        public List<string>? Modules { get; set; }
    }

    // --- Shared hint data (loaded once) ---
    private static readonly Lazy<Dictionary<string, List<ConcreteConformer>>> _sharedHints =
        new(LoadHints);

    public ConcreteSpecializationEngine(ITypeDatabase typeDatabase, string? currentModuleName = null)
    {
        _typeDatabase = typeDatabase;
        _hintConformers = _sharedHints.Value;
        _abiConformers = new Dictionary<string, List<ConcreteConformer>>(StringComparer.Ordinal);
        _currentModuleName = currentModuleName;
    }

    /// <summary>
    /// Indexes module-local conformances from a ModuleDecl's type declarations.
    /// Call once per module before querying specializable methods.
    /// </summary>
    public void IndexModuleConformances(ModuleDecl moduleDecl)
    {
        // Capture the indexed module's identity + dependency list so the plausibility
        // check in VerifyHintAgainstAbi can honor cross-module refinement chains for
        // imported protocols (e.g. a user type refining Swift.Sequence through a
        // local but unindexed helper protocol).
        //
        // Union both ModuleDecl dependency fields: `Dependencies` is the ABI parser's
        // nominal list (currently initialized empty in production — the parser never
        // adds to it), while `DependencyModuleNames` is the resolved
        // --framework-dependency list populated by Program.cs before IndexModuleConformances
        // runs and consumed by ModuleHandler for wrapper imports. Using only one
        // yields false negatives: Dependencies is empty in real runs, and
        // DependencyModuleNames is empty in ABI-parser unit tests. Merging is the
        // single source of truth for "modules this module imports".
        _indexedModuleName = moduleDecl.Name;
        _indexedModuleDependencies = new HashSet<string>(
            moduleDecl.Dependencies, StringComparer.Ordinal);
        foreach (var dep in moduleDecl.DependencyModuleNames)
            _indexedModuleDependencies.Add(dep);

        // First pass: index protocol declarations so transitive-conformance checks at
        // query time can walk ProtocolDecl.InheritedProtocols. Types may appear before
        // their declared protocols in the module, so we index protocols up front.
        foreach (var typeDecl in moduleDecl.Types)
            IndexProtocolDecls(typeDecl);
        foreach (var typeDecl in moduleDecl.Types)
            IndexTypeConformances(typeDecl);
    }

    private void IndexProtocolDecls(TypeDecl typeDecl)
    {
        if (typeDecl is ProtocolDecl pd && pd.SwiftTypeName is { } name)
            _abiProtocols[name.ToString()] = pd;
        foreach (var nested in typeDecl.Types)
            IndexProtocolDecls(nested);
    }

    private void IndexTypeConformances(TypeDecl typeDecl)
    {
        // Skip generic types — we can't use them as concrete specializers
        // because the generated Swift wrapper would have unresolved type parameters
        // (e.g., ArraySection<Model, Element>.self without concrete type arguments)
        if (typeDecl.IsGeneric)
        {
            // Still recurse into nested types (a non-generic nested type is valid)
            foreach (var nested in typeDecl.Types)
                IndexTypeConformances(nested);
            return;
        }

        IEnumerable<TypeConformance> conformances = typeDecl switch
        {
            StructDecl sd => sd.Conformances,
            ClassDecl cd => cd.Conformances,
            EnumDecl ed => ed.Conformances,
            _ => Enumerable.Empty<TypeConformance>()
        };

        if (typeDecl.SwiftTypeName is { } indexedTypeName)
        {
            var indexedKey = indexedTypeName.ToString();
            _abiIndexedTypes.Add(indexedKey);
            if (!_abiDeclaredProtocolsByType.TryGetValue(indexedKey, out var declaredSet))
            {
                declaredSet = new HashSet<string>(StringComparer.Ordinal);
                _abiDeclaredProtocolsByType[indexedKey] = declaredSet;
            }
            foreach (var c in conformances)
                declaredSet.Add(c.Protocol.ToString());
        }

        foreach (var conformance in conformances)
        {
            var protocolKey = conformance.Protocol.ToString();
            if (!_abiConformers.ContainsKey(protocolKey))
                _abiConformers[protocolKey] = new List<ConcreteConformer>();

            // Resolve C# type name
            var csName = ResolveCSharpName(conformance.ConformingType);
            if (csName != null)
            {
                _abiConformers[protocolKey].Add(new ConcreteConformer(
                    conformance.ConformingType.ToString(),
                    csName,
                    conformance.ConformingType,
                    AvailabilityAnnotations: CollectAvailability(typeDecl)));
            }
        }

        // Recurse into nested types
        foreach (var nested in typeDecl.Types)
            IndexTypeConformances(nested);
    }

    private static IReadOnlyList<AvailabilityAnnotation>? CollectAvailability(TypeDecl typeDecl)
    {
        List<AvailabilityAnnotation>? merged = null;
        BaseDecl? current = typeDecl;
        while (current is TypeDecl td)
        {
            if (td.AvailabilityAnnotations is { Count: > 0 } parentAnnotations)
            {
                merged ??= new List<AvailabilityAnnotation>();
                merged.AddRange(parentAnnotations);
            }
            current = td.ParentDecl;
        }
        return merged;
    }

    /// <summary>
    /// Returns true if the conformer is allowed to apply while generating bindings for
    /// <paramref name="moduleFilter"/>. A conformer with no <see cref="ConcreteConformer.AllowedModules"/>
    /// set is global — it always applies. A conformer with a non-empty allow-list only applies
    /// when <paramref name="moduleFilter"/> is non-null and listed. Scoped conformers FAIL
    /// CLOSED when the caller passes no module context: without a filter we cannot prove the
    /// conformer belongs to the module being generated, so we refuse to apply it. The previous
    /// behavior (null = let-every-hint-through) leaked scoped conformers into unrelated
    /// consumer modules.
    /// </summary>
    internal static bool IsConformerAllowedForModule(ConcreteConformer conformer, string? moduleFilter)
    {
        if (conformer.AllowedModules is null || conformer.AllowedModules.Count == 0) return true;
        if (moduleFilter is null) return false;
        foreach (var allowed in conformer.AllowedModules)
        {
            if (string.Equals(allowed, moduleFilter, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether the specialization hints registry has any conformer for the protocol
    /// that is allowed in <paramref name="moduleFilter"/>. Scoped hints require a matching
    /// module filter; a null filter only matches unscoped (global) hints.
    /// </summary>
    public static bool HasKnownHintConformers(string protocolQualifiedName, string? moduleFilter = null)
    {
        if (!_sharedHints.Value.TryGetValue(protocolQualifiedName, out var list) || list.Count == 0)
            return false;
        foreach (var c in list)
        {
            if (IsConformerAllowedForModule(c, moduleFilter)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns hint-registered conformers for a protocol, filtered to those that apply while
    /// generating bindings for <paramref name="moduleFilter"/>. Scoped conformers fail closed
    /// when no filter is passed (see <see cref="IsConformerAllowedForModule"/>).
    /// </summary>
    public static IReadOnlyList<ConcreteConformer> GetHintConformers(string protocolQualifiedName, string? moduleFilter = null)
    {
        if (!_sharedHints.Value.TryGetValue(protocolQualifiedName, out var list))
            return Array.Empty<ConcreteConformer>();
        var filtered = new List<ConcreteConformer>(list.Count);
        foreach (var c in list)
        {
            if (IsConformerAllowedForModule(c, moduleFilter))
                filtered.Add(c);
        }
        return filtered;
    }

    /// <summary>
    /// Returns known concrete conformers for a protocol, combining hints and ABI sources.
    /// Hint conformers with an allow-list are filtered against the engine's current module
    /// name (set at construction). ABI conformers are always included — they come from the
    /// module being generated, so module scoping is implicit.
    /// </summary>
    public List<ConcreteConformer> GetConformers(SwiftTypeName protocolName)
    {
        var result = new List<ConcreteConformer>();

        var key = protocolName.ToString();

        _abiConformers.TryGetValue(key, out var abiList);

        if (_hintConformers.TryGetValue(key, out var hintList))
        {
            foreach (var c in hintList)
            {
                if (!IsConformerAllowedForModule(c, _currentModuleName))
                    continue;

                // Cross-check hint-declared conformers against the current module's ABI.
                // If the conformer type was indexed from this module but none of its
                // declared conformances is `protocolName` or inherits it, the hint is a
                // false positive (common when a hint outlives an SDK change or was wrong
                // from the start — e.g. MusicKit.MusicVideo wrongly listed under
                // PlayableMusicItem). Trusting the hint produces an uncompilable Swift
                // wrapper. The check walks ProtocolDecl.InheritedProtocols transitively
                // so hints remain valid when the type declares a refining protocol.
                //
                // Conformers not indexed in this module, or whose refinement chain walks
                // into a protocol from another module, yield Uncertain — the hint is
                // kept because we have no ground truth to disprove it.
                if (VerifyHintAgainstAbi(c.SwiftQualifiedName, key) == AbiVerification.Disproved)
                    continue;

                result.Add(c);
            }
        }

        if (abiList is not null)
        {
            // Availability augmentation: hints do not carry @available floors (hints are
            // authored by hand in specialization-hints.json with no platform data). When
            // the same conformer is indexed from the current module's ABI, copy its
            // availability annotations onto the hint result so CSM wrappers emit the
            // correct @available floor. Without this, hint-declared conformers like
            // CryptoKit.SHA3_256 (iOS 26 / macOS 15) get specialized with the containing
            // HMAC type's iOS 13 floor, producing Swift compiler "only available in iOS
            // 26" errors at wrapper compile time.
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].AvailabilityAnnotations is { Count: > 0 }) continue;
                ConcreteConformer? match = null;
                foreach (var a in abiList)
                {
                    if (string.Equals(a.SwiftQualifiedName, result[i].SwiftQualifiedName, StringComparison.Ordinal))
                    {
                        match = a;
                        break;
                    }
                }
                if (match?.AvailabilityAnnotations is { Count: > 0 } matchAvail)
                {
                    result[i] = result[i] with { AvailabilityAnnotations = matchAvail };
                }
            }

            // Dedup: don't add ABI conformers that are already in hints
            var existingTypes = new HashSet<string>(result.Select(c => c.SwiftQualifiedName));
            foreach (var conformer in abiList)
            {
                if (!existingTypes.Contains(conformer.SwiftQualifiedName))
                    result.Add(conformer);
            }
        }

        return result;
    }

    private enum AbiVerification
    {
        Confirmed,
        Disproved,
        Uncertain,
    }

    /// <summary>
    /// Checks whether <paramref name="conformerQualifiedName"/> is known, from the current
    /// module's ABI, to conform to <paramref name="protocolKey"/>. Walks the declared
    /// protocols' <see cref="ProtocolDecl.InheritedProtocols"/> chains transitively so
    /// a type declaring a refining protocol still verifies for the refined one.
    /// Returns <see cref="AbiVerification.Uncertain"/> whenever the conformer is not
    /// indexed in our module (no ground truth), or when an indexed chain reaches an
    /// unindexed protocol that could plausibly refine the target (same-module
    /// plausibility). Unrelated external conformances — e.g. Swift.Hashable /
    /// Swift.Sendable declared on a user type against a user protocol target — do NOT
    /// preserve uncertainty: a protocol in module X can only refine a protocol in
    /// module Y if X imports Y, so cross-module refinement of an arbitrary external
    /// protocol to a user target is implausible.
    /// </summary>
    private AbiVerification VerifyHintAgainstAbi(string conformerQualifiedName, string protocolKey)
    {
        if (!_abiIndexedTypes.Contains(conformerQualifiedName))
            return AbiVerification.Uncertain;

        if (!_abiDeclaredProtocolsByType.TryGetValue(conformerQualifiedName, out var declared)
            || declared.Count == 0)
        {
            return AbiVerification.Disproved;
        }

        bool anyUncertain = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaredProtocol in declared)
        {
            switch (ProtocolChainContains(declaredProtocol, protocolKey, visited))
            {
                case AbiVerification.Confirmed:
                    return AbiVerification.Confirmed;
                case AbiVerification.Uncertain:
                    anyUncertain = true;
                    break;
            }
        }
        return anyUncertain ? AbiVerification.Uncertain : AbiVerification.Disproved;
    }

    private AbiVerification ProtocolChainContains(string protocolKey, string targetKey, HashSet<string> visited)
    {
        if (string.Equals(protocolKey, targetKey, StringComparison.Ordinal))
            return AbiVerification.Confirmed;
        if (!visited.Add(protocolKey))
            return AbiVerification.Disproved;
        if (!_abiProtocols.TryGetValue(protocolKey, out var pd))
        {
            // Unindexed protocol — we cannot walk its InheritedProtocols. Preserve
            // uncertainty only when refinement to the target is plausible. A protocol
            // P in module M can refine a protocol Q in module N only if M imports N
            // (directly or transitively). We approximate "M imports N" as:
            //   - M == N (same-module refinement, always plausible), OR
            //   - M is the current indexed module AND N is Swift stdlib OR a listed
            //     dependency (ModuleDecl.Dependencies gives at least partial ground
            //     truth for what the current module imports).
            // Otherwise Disproved — this prevents unrelated conformances
            // (Swift.Hashable, Sendable, Codable) from masking false-positive hints
            // against user protocols, while still allowing a local unparsed helper
            // protocol to refine an imported target like Swift.Sequence.
            return IsPlausibleRefiner(protocolKey, targetKey)
                ? AbiVerification.Uncertain
                : AbiVerification.Disproved;
        }

        bool anyUncertain = false;
        foreach (var inherited in pd.InheritedProtocols)
        {
            switch (ProtocolChainContains(inherited.Name, targetKey, visited))
            {
                case AbiVerification.Confirmed:
                    return AbiVerification.Confirmed;
                case AbiVerification.Uncertain:
                    anyUncertain = true;
                    break;
            }
        }
        return anyUncertain ? AbiVerification.Uncertain : AbiVerification.Disproved;
    }

    private bool IsPlausibleRefiner(string refinerQualifiedName, string targetQualifiedName)
    {
        var refinerModule = ModuleOf(refinerQualifiedName);
        var targetModule = ModuleOf(targetQualifiedName);
        if (refinerModule is null || targetModule is null) return false;

        // Same-module refinement is always plausible — the refiner's module can see
        // the target declaration directly.
        if (string.Equals(refinerModule, targetModule, StringComparison.Ordinal))
            return true;

        // Cross-module: only plausible when the refiner's module imports the target's.
        // We only have ground truth for the module currently being indexed, so we can
        // only answer plausibility when the refiner lives in the indexed module.
        if (_indexedModuleName is null
            || !string.Equals(refinerModule, _indexedModuleName, StringComparison.Ordinal))
            return false;

        // Swift stdlib is implicitly imported by every Swift module.
        if (string.Equals(targetModule, "Swift", StringComparison.Ordinal)) return true;

        // Otherwise defer to the indexed module's declared dependency list.
        return _indexedModuleDependencies is not null
            && _indexedModuleDependencies.Contains(targetModule);
    }

    private static string? ModuleOf(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        return dot <= 0 ? null : qualifiedName.Substring(0, dot);
    }

    /// <summary>
    /// Finds methods on a type that can be concretely specialized.
    /// A method is specializable if it has method-own generic parameters constrained
    /// to protocols with associated types or Self requirements, AND all such protocols
    /// have at least one known conformer.
    /// </summary>
    public List<SpecializableMethod> FindSpecializableMethods(TypeDecl typeDecl)
    {
        var result = new List<SpecializableMethod>();

        // Session 2: resolve parent-generic specializable params when the parent is
        // generic. All parent generics MUST have hint-resolved conformers — partial
        // resolution would produce half-specialized Swift wrappers whose `self_`
        // conversion references unresolved type parameters. When any parent generic is
        // unresolvable, fall through to the existing path which skips CSM entirely for
        // this type (no method-own specializable params will be emitted either, because
        // the emitter's generic-parent gate still catches them defensively).
        List<SpecializableParam>? parentSpecializableParams = null;
        if (typeDecl.IsGeneric)
        {
            parentSpecializableParams = ResolveParentSpecializableParams(typeDecl);
        }

        // Collect parent type generic parameter names
        var parentParamNames = typeDecl.IsGeneric
            ? new HashSet<string>(typeDecl.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        foreach (var method in typeDecl.Methods)
        {
            // Find method-own generic params (not inherited from parent type). Methods
            // on a generic parent may carry parent-inherited entries in their
            // GenericParameters list (see cross-level coupling at the bottom of the
            // method-own block) so we filter explicitly rather than gating on
            // method.IsGeneric — that gate would mis-classify a plain sync method on
            // a PAT-constrained generic parent as ineligible for parent-only CSM.
            var ownParams = method.GenericParameters
                .Where(p => !parentParamNames.Contains(p.TypeName))
                .ToList();

            // Parent-only path: method has no own generics but the parent is generic
            // and every parent generic resolved cleanly. Build a SpecializableMethod
            // whose only specializable dimension is the parent-conformer cartesian.
            // The emitter's methodParams.Count == 0 branch in
            // EmitConcreteSpecializationsForGenericParent emits one overload per
            // parent tuple; the sync-eligibility predicate already handles the
            // ownParamCount==0 case (parity check 0 != 0 is false). Sync-only for
            // session 2 — async/static/accessor/constructor are filtered at the
            // emitter and predicate sites, so it's harmless to register them here.
            if (ownParams.Count == 0)
            {
                if (parentSpecializableParams is null) continue;
                result.Add(new SpecializableMethod(method,
                    new List<SpecializableParam>(parentSpecializableParams)));
                continue;
            }

            // Original path requires at least one method-own generic. method.IsGeneric
            // is implied here (ownParams.Count > 0 forces GenericParameters.Count > 0).

            var ownParamNames = new HashSet<string>(
                ownParams.Select(p => p.TypeName), StringComparer.Ordinal);

            // Discover cross-param same-type couplings like `S.Element == T`. These
            // come in two ABI forms:
            //   LHS: stored on S's AssosiatedTypeConformances with Path=["Element",...]
            //        and ConformanceTarget = bare param name (module="", name="T").
            //   RHS: stored on T's GenericConformances with Path.Length==1 and
            //        ConformanceTarget = module-qualified member ("S.Element").
            // Either form produces the same logical coupling on S:
            //   (AssocName="Element", OtherParamName="T").
            // We record them on the param that owns the associated type (S), and
            // enforce them at cartesian pairing time in ConformerPairingSatisfiesCoupling.
            var couplingsByParam = new Dictionary<string, List<(string AssocName, string OtherParamName)>>(
                StringComparer.Ordinal);

            void AddCoupling(string paramName, string assocName, string otherParamName)
            {
                if (!couplingsByParam.TryGetValue(paramName, out var list))
                {
                    list = new List<(string, string)>();
                    couplingsByParam[paramName] = list;
                }
                var entry = (assocName, otherParamName);
                if (!list.Contains(entry))
                    list.Add(entry);
            }

            // Coupling targets can name either a method-own generic or a parent-generic
            // param. Restricting to method-own names misses `D.Element == T` style
            // constraints where T is the parent's generic — leaving that coupling
            // unenforced and producing invalid Swift cross-paired specializations.
            var couplingTargetNames = new HashSet<string>(ownParamNames, StringComparer.Ordinal);
            if (parentSpecializableParams is not null)
            {
                foreach (var pp in parentSpecializableParams)
                    couplingTargetNames.Add(pp.GenericParam.TypeName);
            }

            foreach (var param in ownParams)
            {
                var paramName = param.TypeName;

                foreach (var c in param.AssosiatedTypeConformances)
                {
                    if (c.Kind != ConformanceKind.ConcreteType) continue;
                    if (c.Path.Length < 2) continue;
                    var target = c.ConformanceTarget;
                    if (!string.IsNullOrEmpty(target.Module)) continue;
                    if (!couplingTargetNames.Contains(target.Name)) continue;
                    if (target.Name == paramName) continue;
                    AddCoupling(paramName, c.Path[^1], target.Name);
                }

                foreach (var c in param.GenericConformances)
                {
                    if (c.Kind != ConformanceKind.ConcreteType) continue;
                    if (c.Path.Length != 1) continue;
                    var target = c.ConformanceTarget;
                    if (string.IsNullOrEmpty(target.Module)) continue;
                    if (!couplingTargetNames.Contains(target.Module)) continue;
                    if (target.Module == paramName) continue;
                    AddCoupling(target.Module, target.Name, paramName);
                }
            }

            // Cross-level coupling: `τ_0_0 == τ_1_0.Element` (parent T constrained to S's
            // associated type) lives on the METHOD-LEVEL GenericArgumentDecl for τ_0_0
            // (not the struct's τ_0_0, whose generic signature only carries the struct's
            // own where-clause). It's stored on GenericConformances with Path=["τ_0_0"]
            // and ConformanceTarget="τ_1_0.Element" (Module="τ_1_0", Name="Element").
            // Neither ResolveParentSpecializableParams (sees only struct-level conformances)
            // nor the method-own loops above (iterate ownParams only) pick it up, so
            // without this block the coupling goes unrecorded and the cartesian emits
            // invalid pairings (e.g. T=SongItem with S=Data whose Element is UInt8, not
            // SongItem) that Swift then rejects with "requires the types 'SongItem' and
            // 'Data.Element' (aka 'UInt8') be equivalent". Record the coupling on the
            // method-own param (S) so ConformerPairingSatisfiesCoupling can enforce it.
            foreach (var methodLevelParam in method.GenericParameters)
            {
                var methodLevelName = methodLevelParam.TypeName;
                if (!parentParamNames.Contains(methodLevelName)) continue;

                foreach (var c in methodLevelParam.GenericConformances)
                {
                    if (c.Kind != ConformanceKind.ConcreteType) continue;
                    if (c.Path.Length != 1) continue;
                    var target = c.ConformanceTarget;
                    if (string.IsNullOrEmpty(target.Module)) continue;
                    if (!ownParamNames.Contains(target.Module)) continue;
                    if (target.Module == methodLevelName) continue;
                    AddCoupling(target.Module, target.Name, methodLevelName);
                }
            }

            var specializableParams = new List<SpecializableParam>();

            foreach (var param in ownParams)
            {
                // Collect same-type constraints on the param's associated types
                // (e.g., τ_0_0.Element == Swift.String). Targets that point at another
                // coupled generic param (own OR parent — bare name, no module) are
                // couplings and filtered out here; they're enforced after cartesian
                // pairing is known via CouplingConstraints. Filtering only on
                // ownParamNames would leave `D.Element == T` (parent T) surviving as
                // a literal "T" concrete constraint and empty usableConformers.
                var associatedConstraints = param.AssosiatedTypeConformances
                    .Where(c => c.Kind == ConformanceKind.ConcreteType && c.Path.Length >= 2)
                    .Where(c =>
                    {
                        var t = c.ConformanceTarget;
                        if (!string.IsNullOrEmpty(t.Module)) return true;
                        return !couplingTargetNames.Contains(t.Name);
                    })
                    .Select(c => (Name: c.Path[^1], Target: c.ConformanceTarget.ToString()))
                    .ToList();

                // Find the first protocol constraint with known conformers.
                // Checks both "unsupported" protocols (PAT/Self) and any protocol
                // that has conformers in the hints or ABI — this enables specialization
                // for module-local protocols that may not be in the type database.
                var protocolConstraint = FindSpecializableProtocolConstraint(param);
                if (protocolConstraint == null) continue;

                var conformers = GetConformers(protocolConstraint);
                if (conformers.Count == 0) continue;

                // Multi-constraint intersection: FindSpecializableProtocolConstraint picks
                // ONE protocol per param. When the param carries additional constraints
                // (e.g. `<T : Decodable, T : MusicRecentlyPlayedRequestable>`) we must
                // intersect against the others or we leak conformers of the selected
                // protocol that fail the rest — emitting CSM overloads the Swift wrapper
                // cannot compile. Rejected pairings are recorded so the emission report
                // can surface them.
                var allConstraints = CollectAllProtocolConstraints(param);

                // Filter conformers to those whose types are resolvable
                var usableConformers = new List<ConcreteConformer>(conformers.Count);
                foreach (var c in conformers)
                {
                    if (c.CSharpType == null || IsCSharpPrimitiveType(c.CSharpType)) continue;
                    if (!ConformerSatisfiesAssociatedTypes(c, associatedConstraints)) continue;
                    if (!ConformerSatisfiesAllConstraints(c, allConstraints, protocolConstraint, out var missingConstraint))
                    {
                        RecordRejection(typeDecl, param, protocolConstraint, c, missingConstraint!);
                        continue;
                    }
                    usableConformers.Add(c);
                }

                if (usableConformers.Count == 0) continue;

                couplingsByParam.TryGetValue(param.TypeName, out var paramCouplings);

                specializableParams.Add(new SpecializableParam(
                    param, protocolConstraint, usableConformers, paramCouplings));
            }

            // Only specializable if at least one method-own param has conformers.
            // When the parent is generic, prepend parent-specializable params so the
            // pairing drives both parent and method concretization in one cartesian
            // product. When parent generics were not resolvable (parentSpecializableParams
            // is null), skip — the emitter's gate catches this as a defensive second line
            // but ignoring here avoids wasted cartesian expansion on hopeless pairings.
            if (specializableParams.Count > 0)
            {
                if (typeDecl.IsGeneric && parentSpecializableParams is null)
                    continue;

                var combined = parentSpecializableParams is null
                    ? specializableParams
                    : new List<SpecializableParam>(parentSpecializableParams.Count + specializableParams.Count);
                if (parentSpecializableParams is not null)
                {
                    combined.AddRange(parentSpecializableParams);
                    combined.AddRange(specializableParams);
                }

                result.Add(new SpecializableMethod(method, combined));
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves parent-type generic parameters into <see cref="SpecializableParam"/>
    /// entries when every parent generic has at least one usable hint-resolved conformer.
    /// Returns null if any parent generic lacks resolvable conformers — all-or-nothing
    /// semantics prevent emitting half-specialized wrappers.
    /// </summary>
    private List<SpecializableParam>? ResolveParentSpecializableParams(TypeDecl typeDecl)
    {
        var parentParamNames = new HashSet<string>(
            typeDecl.GenericParameters.Select(p => p.TypeName), StringComparer.Ordinal);

        // Discover parent↔parent same-type couplings (e.g. `S.Element == T` where both
        // S and T are parent generics). Same two ABI forms as the method-own path:
        // LHS on AssosiatedTypeConformances (bare-name target), RHS on GenericConformances
        // (module-qualified member). Enforced at cartesian pairing time by
        // ConformerPairingSatisfiesCoupling via CouplingConstraints.
        var parentCouplings = new Dictionary<string, List<(string AssocName, string OtherParamName)>>(
            StringComparer.Ordinal);

        void AddCoupling(string paramName, string assocName, string otherParamName)
        {
            if (!parentCouplings.TryGetValue(paramName, out var list))
            {
                list = new List<(string, string)>();
                parentCouplings[paramName] = list;
            }
            var entry = (assocName, otherParamName);
            if (!list.Contains(entry))
                list.Add(entry);
        }

        foreach (var parentParam in typeDecl.GenericParameters)
        {
            var paramName = parentParam.TypeName;

            foreach (var c in parentParam.AssosiatedTypeConformances)
            {
                if (c.Kind != ConformanceKind.ConcreteType) continue;
                if (c.Path.Length < 2) continue;
                var target = c.ConformanceTarget;
                if (!string.IsNullOrEmpty(target.Module)) continue;
                if (!parentParamNames.Contains(target.Name)) continue;
                if (target.Name == paramName) continue;
                AddCoupling(paramName, c.Path[^1], target.Name);
            }

            foreach (var c in parentParam.GenericConformances)
            {
                if (c.Kind != ConformanceKind.ConcreteType) continue;
                if (c.Path.Length != 1) continue;
                var target = c.ConformanceTarget;
                if (string.IsNullOrEmpty(target.Module)) continue;
                if (!parentParamNames.Contains(target.Module)) continue;
                if (target.Module == paramName) continue;
                AddCoupling(target.Module, target.Name, paramName);
            }
        }

        var resolved = new List<SpecializableParam>();
        foreach (var parentParam in typeDecl.GenericParameters)
        {
            var protocol = FindSpecializableProtocolConstraint(parentParam);
            if (protocol is null) return null;

            var conformers = GetConformers(protocol);
            if (conformers.Count == 0) return null;

            // Mirror the method-own path: same-type constraints on the parent generic's
            // associated types (e.g. `T.Element == Swift.String`) narrow the usable
            // conformer set. Targets that name another parent-generic param (bare name,
            // no module) are cross-param couplings and filtered out here — they're
            // enforced at cartesian pairing time via CouplingConstraints.
            var associatedConstraints = parentParam.AssosiatedTypeConformances
                .Where(c => c.Kind == ConformanceKind.ConcreteType && c.Path.Length >= 2)
                .Where(c =>
                {
                    var t = c.ConformanceTarget;
                    if (!string.IsNullOrEmpty(t.Module)) return true;
                    return !parentParamNames.Contains(t.Name);
                })
                .Select(c => (Name: c.Path[^1], Target: c.ConformanceTarget.ToString()))
                .ToList();

            // Multi-constraint intersection at the parent-generic pairing step. Same
            // mechanism as the method-own path in FindSpecializableMethods: a parent
            // generic param with multiple protocol constraints needs its conformer set
            // filtered against every constraint, not just the one
            // FindSpecializableProtocolConstraint selected.
            var allParentConstraints = CollectAllProtocolConstraints(parentParam);

            var usable = new List<ConcreteConformer>(conformers.Count);
            foreach (var c in conformers)
            {
                if (c.CSharpType == null || IsCSharpPrimitiveType(c.CSharpType)) continue;
                if (!ConformerSatisfiesAssociatedTypes(c, associatedConstraints)) continue;
                if (!ConformerSatisfiesAllConstraints(c, allParentConstraints, protocol, out var missingConstraint))
                {
                    RecordRejection(typeDecl, parentParam, protocol, c, missingConstraint!);
                    continue;
                }
                if (ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(c, _typeDatabase)
                    != ConcreteProtocolSpecializationEmitter.StructuralEmitReject.None) continue;
                usable.Add(c);
            }
            if (usable.Count == 0) return null;

            parentCouplings.TryGetValue(parentParam.TypeName, out var couplings);

            resolved.Add(new SpecializableParam(
                parentParam,
                protocol,
                usable,
                CouplingConstraints: couplings,
                IsParentGeneric: true));
        }
        return resolved;
    }

    /// <summary>
    /// Checks if a method can be concretely specialized. Lightweight check for validation pipeline.
    /// </summary>
    public bool CanSpecialize(MethodDecl method)
    {
        if (!method.IsGeneric) return false;
        if (method.ParentDecl is not TypeDecl parentType) return false;

        var parentParamNames = parentType.IsGeneric
            ? new HashSet<string>(parentType.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        var ownParams = method.GenericParameters
            .Where(p => !parentParamNames.Contains(p.TypeName))
            .ToList();

        if (ownParams.Count == 0) return false;

        foreach (var param in ownParams)
        {
            var protocol = FindSpecializableProtocolConstraint(param);
            if (protocol == null) continue;

            var conformers = GetConformers(protocol);
            if (conformers.Count == 0) return false;
        }

        return true;
    }

    // --- Private helpers ---

    /// <summary>
    /// Finds a protocol constraint on a generic param that has known conformers.
    /// First prefers "unsupported" protocols (PAT/Self) as those block normal emission.
    /// Falls back to any protocol with known conformers.
    /// </summary>
    private SwiftTypeName? FindSpecializableProtocolConstraint(GenericArgumentDecl param)
    {
        SwiftTypeName? anyProtocolWithConformers = null;

        foreach (var conformance in param.GenericConformances)
        {
            if (conformance.Kind != ConformanceKind.Protocol) continue;

            // Prefer unsupported protocols (these block normal emission)
            if (MethodValidationGates.IsUnsupportedProtocolConstraint(
                conformance.ConformanceTarget, _typeDatabase))
            {
                return conformance.ConformanceTarget;
            }

            // Track any protocol that has known conformers as fallback
            if (anyProtocolWithConformers == null && GetConformers(conformance.ConformanceTarget).Count > 0)
            {
                anyProtocolWithConformers = conformance.ConformanceTarget;
            }
        }

        return anyProtocolWithConformers;
    }

    private string? ResolveCSharpName(SwiftTypeName swiftTypeName)
    {
        if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record.CSharpTypeName.FullyQualifiedName;
        // Don't fall back to raw name — types not in the database may not have
        // C# bindings (e.g., suppressed nested types like CS.BigUInt.Words).
        return null;
    }

    /// <summary>
    /// Returns true if the conformer's declared AssociatedTypes match the given
    /// same-type constraints. A conformer with no declared AssociatedTypes is
    /// accepted only when the constraint list is empty (we can't verify otherwise).
    /// </summary>
    private static bool ConformerSatisfiesAssociatedTypes(
        ConcreteConformer conformer,
        List<(string Name, string Target)> constraints)
    {
        if (constraints.Count == 0) return true;
        if (conformer.AssociatedTypes is null) return false;

        foreach (var (name, target) in constraints)
        {
            if (!conformer.AssociatedTypes.TryGetValue(name, out var conformerTarget))
                return false;
            if (!string.Equals(conformerTarget, target, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool IsCSharpPrimitiveType(string typeName)
    {
        return typeName switch
        {
            "int" or "uint" or "long" or "ulong" or "short" or "ushort" or
            "byte" or "sbyte" or "float" or "double" or "bool" or "char" or
            "nint" or "nuint" or "decimal" or "string" => true,
            _ => false
        };
    }

    /// <summary>
    /// Collects every protocol the generic param is constrained to (the full PAT bag),
    /// returning the qualified protocol names in declaration order.
    /// <see cref="FindSpecializableProtocolConstraint"/> selects only one — we need the
    /// rest here so the pairing filter can intersect the conformer set against every
    /// constraint, not just the selected one. Same-type constraints (Kind != Protocol)
    /// and reflexive parent-Self-reference rows produced by the parser are skipped.
    /// </summary>
    private static List<SwiftTypeName> CollectAllProtocolConstraints(GenericArgumentDecl param)
    {
        var protocols = new List<SwiftTypeName>();
        foreach (var conformance in param.GenericConformances)
        {
            if (conformance.Kind != ConformanceKind.Protocol) continue;
            // Skip same-type couplings encoded with empty target (defensive — the public
            // ABI shape always carries Module/Name on Protocol-kind conformances, but
            // a malformed entry must not poison the constraint list).
            var target = conformance.ConformanceTarget;
            if (string.IsNullOrEmpty(target.Name)) continue;
            protocols.Add(target);
        }
        return protocols;
    }

    /// <summary>
    /// Returns true when <paramref name="conformer"/> satisfies every protocol in
    /// <paramref name="allConstraints"/> per current-module ABI knowledge. The
    /// selected protocol — the one returned by
    /// <see cref="FindSpecializableProtocolConstraint"/> and used to populate
    /// <paramref name="conformer"/> — is skipped because membership in its conformer
    /// list is the precondition for arriving here.
    /// <para>
    /// Each non-selected constraint is checked with <see cref="VerifyHintAgainstAbi"/>.
    /// <see cref="AbiVerification.Disproved"/> rejects the conformer — its declared
    /// protocols (plus their <see cref="ProtocolDecl.InheritedProtocols"/> chains) do
    /// not include the constraint. <see cref="AbiVerification.Uncertain"/> accepts
    /// (we lack ground truth in this module — e.g. the conformer is hint-declared
    /// from outside the indexed module, or the chain walks into an unindexed protocol
    /// from a plausible-refiner module). <see cref="AbiVerification.Confirmed"/>
    /// accepts.
    /// </para>
    /// <para>
    /// When the conformer is rejected, <paramref name="missingConstraint"/> receives
    /// the qualified name of the failing constraint; otherwise it is set to <c>null</c>.
    /// The caller is expected to record the rejection on
    /// <see cref="_rejectedPairings"/> so the emission report can surface it.
    /// </para>
    /// </summary>
    private bool ConformerSatisfiesAllConstraints(
        ConcreteConformer conformer,
        IReadOnlyList<SwiftTypeName> allConstraints,
        SwiftTypeName selectedProtocol,
        out string? missingConstraint)
    {
        missingConstraint = null;
        var selectedKey = selectedProtocol.ToString();
        foreach (var constraint in allConstraints)
        {
            var constraintKey = constraint.ToString();
            if (string.Equals(constraintKey, selectedKey, StringComparison.Ordinal)) continue;

            if (VerifyHintAgainstAbi(conformer.SwiftQualifiedName, constraintKey) == AbiVerification.Disproved)
            {
                missingConstraint = constraintKey;
                return false;
            }
        }
        return true;
    }

    private void RecordRejection(
        TypeDecl parentTypeDecl,
        GenericArgumentDecl param,
        SwiftTypeName selectedProtocol,
        ConcreteConformer conformer,
        string missingConstraint)
    {
        var parentKey = parentTypeDecl.SwiftTypeName?.ToString() ?? parentTypeDecl.Name;
        _rejectedPairings.Add(new CsmRejectedPairing(
            ParentType: parentKey,
            GenericParamName: param.TypeName,
            SelectedProtocol: selectedProtocol.ToString(),
            ConformerSwiftType: conformer.SwiftQualifiedName,
            MissingConstraint: missingConstraint,
            Reason: "conformer does not satisfy non-selected protocol constraint per current-module ABI"));
    }

    private void RecordMethodWhereRejection(
        TypeDecl parentTypeDecl,
        MethodDecl method,
        GenericArgumentDecl param,
        SwiftTypeName selectedProtocol,
        ConcreteConformer conformer,
        string missingConstraint)
    {
        var parentKey = parentTypeDecl.SwiftTypeName?.ToString() ?? parentTypeDecl.Name;
        _rejectedPairings.Add(new CsmRejectedPairing(
            ParentType: parentKey,
            GenericParamName: param.TypeName,
            SelectedProtocol: selectedProtocol.ToString(),
            ConformerSwiftType: conformer.SwiftQualifiedName,
            MissingConstraint: missingConstraint,
            Reason: $"method '{method.Name}' where-clause adds {param.TypeName}:{missingConstraint} not satisfied by parent specialization"));
    }

    /// <summary>
    /// Returns true when every parent-generic conformer in <paramref name="parentTuple"/>
    /// satisfies the method-level where-clause constraints declared on its corresponding
    /// parent generic param. Returns false (and records a rejection) when any conformer
    /// fails a per-method constraint.
    /// <para>
    /// Background: parent-only CSM emits one overload per parent tuple per method. Some
    /// methods carry stricter constraints than the parent type (e.g.
    /// <c>MusicCatalogResourceRequest.init() where MusicItemType : MusicCatalogTopLevelResourceRequesting</c>
    /// or <c>DataResponsePublisher.init(_:queue:) where Value == Data?</c>).
    /// The parent-tuple resolution checks parent-type-level constraints only; per-method
    /// strictness lives in <see cref="MethodDecl.RawGenericSig"/>. Without this gate, the
    /// generated Swift wrapper emits <c>MusicCatalogResourceRequest&lt;Album&gt;()</c> or
    /// <c>DataResponsePublisher&lt;URLEncoding&gt;(...)</c> against constraints the chosen
    /// conformer does not satisfy, producing a hard Swift compile error.
    /// </para>
    /// <para>
    /// Both conformance (<c>:</c>) and same-type (<c>==</c>) clauses are checked.
    /// Conformance clauses already declared at the parent-type level are skipped (the
    /// parent-tuple resolution already enforced them). Same-type clauses always apply —
    /// they're never expressible at the parent-type declaration on a single generic param,
    /// so any <c>τ == SpecificType</c> in the method sig must be verified against the
    /// conformer's Swift name.
    /// </para>
    /// </summary>
    public bool ParentTupleSatisfiesMethodConstraints(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        IReadOnlyList<(SpecializableParam Param, ConcreteConformer Conformer)> parentTuple)
    {
        if (string.IsNullOrEmpty(method.RawGenericSig)) return true;
        var perParam = ParseMethodLevelConstraints(method.RawGenericSig);
        if (perParam.Count == 0) return true;

        foreach (var entry in parentTuple)
        {
            if (!entry.Param.IsParentGeneric) continue;
            var paramName = entry.Param.GenericParam.TypeName;
            if (!perParam.TryGetValue(paramName, out var methodClauses)) continue;

            // Skip protocols already declared at the parent-type level — those were
            // checked at parent-tuple resolution. Only check constraints strictly stricter
            // than the parent type's declaration.
            var parentLevelNames = new HashSet<string>(
                CollectAllProtocolConstraints(entry.Param.GenericParam).Select(p => p.ToString()),
                StringComparer.Ordinal);

            foreach (var (kind, target) in methodClauses)
            {
                if (kind == MethodConstraintKind.Conformance)
                {
                    if (parentLevelNames.Contains(target)) continue;
                    if (VerifyHintAgainstAbi(entry.Conformer.SwiftQualifiedName, target) != AbiVerification.Disproved) continue;
                    RecordMethodWhereRejection(
                        parentTypeDecl, method, entry.Param.GenericParam,
                        entry.Param.ConstraintProtocol, entry.Conformer, target);
                    return false;
                }
                else // SameType: τ == ConcreteType — conformer must equal target
                {
                    // Cross-level coupling clauses with a SameType RHS of the canonical
                    // single-hop dependent-member shape `τ_<d>+_<d>+.<id>` (e.g.
                    // `τ_0_0 == τ_1_0.Element`) are not parent-tuple-only constraints —
                    // they're registered as AddCoupling entries during method specialization
                    // (see lines 668-695) and validated by ConformerPairingSatisfiesCoupling
                    // under the full conformer pairing, where both sides of the equation are
                    // bound. The parent-tuple-only filter has insufficient information to
                    // evaluate them (the method-own side isn't bound yet); re-rejecting here
                    // would discard valid pairings that coupling would have admitted.
                    // Skip — coupling enforces.
                    //
                    // The predicate is restricted to the canonical single-hop shape AND
                    // the RHS root being a method-own param (i.e. NOT a parent-tuple param
                    // name). The latter mirrors the cross-level AddCoupling block at
                    // lines 680-695, which only registers when `target.Module` is in
                    // `ownParamNames` (line 691). Excluded shapes fall through to
                    // literal-compare and reject (fail-closed):
                    //   - Bare `τ_<d>+_<d>+` (no dot): coupling model has no bare-token
                    //     equality entry (it stores `(AssocName, OtherParamName)`).
                    //   - Multi-segment `τ_X_Y.A.B`: ConformerPairingSatisfiesCoupling looks
                    //     up `AssociatedTypes[assocName]` (single hop); chains aren't
                    //     enforced.
                    //   - Parent-parent same-type (RHS root is another parent-tuple param):
                    //     AddCoupling at 680-695 requires RHS root in ownParamNames, so
                    //     parent-parent shapes are not registered.
                    //   - User-defined names starting with τ_ but not matching `τ_<d>+_<d>+`:
                    //     not a generic-parameter placeholder.
                    if (IsCouplingDeferredSameTypeTarget(target, parentTuple)) continue;

                    if (string.Equals(entry.Conformer.SwiftQualifiedName, target, StringComparison.Ordinal)) continue;
                    // Also accept matches against the conformer's literal Swift expression
                    // (covers nested types and Optional/Array/Dictionary printed forms used
                    // by Swift's ABI sig — e.g. "Data?" vs "Swift.Optional<Foundation.Data>").
                    if (!string.IsNullOrEmpty(entry.Conformer.SwiftLiteral) &&
                        string.Equals(entry.Conformer.SwiftLiteral, target, StringComparison.Ordinal)) continue;
                    RecordMethodWhereRejection(
                        parentTypeDecl, method, entry.Param.GenericParam,
                        entry.Param.ConstraintProtocol, entry.Conformer,
                        $"== {target}");
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Canonical Swift generic-parameter-placeholder same-type RHS shape that is
    /// deferred to <c>ConformerPairingSatisfiesCoupling</c>: <c>τ_&lt;d&gt;+_&lt;d&gt;+.&lt;id&gt;</c>
    /// (e.g. <c>τ_1_0.Element</c>). Single-hop only — multi-segment chains are not
    /// covered by the coupling validator and must fall through to fail-closed
    /// rejection.
    /// </summary>
    // Single-hop dependent-member only — matches the exact shape AddCoupling registers;
    // bare-τ and multi-segment fall through to literal-compare and tombstone fail-closed.
    private static readonly System.Text.RegularExpressions.Regex s_couplingDeferredSameTypePattern =
        new(@"^τ_[0-9]+_[0-9]+\.[A-Za-z_][A-Za-z0-9_]*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="target"/> is a SameType RHS whose enforcement is
    /// guaranteed by <c>ConformerPairingSatisfiesCoupling</c>: a single-hop dependent
    /// member <c>τ_&lt;d&gt;+_&lt;d&gt;+.&lt;id&gt;</c> whose RHS root is a method-own
    /// generic (NOT a parent-tuple param). The latter mirrors the cross-level
    /// <c>AddCoupling</c> block's <c>ownParamNames</c> gate — parent-parent same-types
    /// are not registered and must fail-closed.
    /// </summary>
    private static bool IsCouplingDeferredSameTypeTarget(
        string target,
        IReadOnlyList<(SpecializableParam Param, ConcreteConformer Conformer)> parentTuple)
    {
        if (!target.StartsWith("τ_", StringComparison.Ordinal)) return false;
        if (!s_couplingDeferredSameTypePattern.IsMatch(target)) return false;
        var dotIdx = target.IndexOf('.');
        var rhsRoot = target.Substring(0, dotIdx);
        foreach (var entry in parentTuple)
        {
            if (string.Equals(entry.Param.GenericParam.TypeName, rhsRoot, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private enum MethodConstraintKind { Conformance, SameType }

    /// <summary>
    /// Parses <c>MethodDecl.RawGenericSig</c> (format
    /// <c>&lt;τ_0_0, τ_0_1 where τ_0_0 : Module.Protocol, τ_0_1 == Module.ConcreteType&gt;</c>)
    /// into a map of parameter-name → list of (kind, target) pairs. Both conformance
    /// (<c>:</c>) and same-type (<c>==</c>) clauses are captured. Used by
    /// <see cref="ParentTupleSatisfiesMethodConstraints"/> to detect when a per-method
    /// where-clause adds requirements beyond the parent type's declaration.
    /// </summary>
    private static Dictionary<string, List<(MethodConstraintKind Kind, string Target)>> ParseMethodLevelConstraints(string rawGenericSig)
    {
        var result = new Dictionary<string, List<(MethodConstraintKind, string)>>(StringComparer.Ordinal);
        var whereIdx = rawGenericSig.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx < 0) return result;

        var afterWhere = rawGenericSig.Substring(whereIdx + " where ".Length).TrimEnd('>').Trim();
        foreach (var rawClause in afterWhere.Split(','))
        {
            var clause = rawClause.Trim();
            MethodConstraintKind kind;
            int opIdx;
            int opLen;
            var eqIdx = clause.IndexOf("==", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                kind = MethodConstraintKind.SameType;
                opIdx = eqIdx;
                opLen = 2;
            }
            else
            {
                var colonIdx = clause.IndexOf(':');
                if (colonIdx <= 0) continue;
                kind = MethodConstraintKind.Conformance;
                opIdx = colonIdx;
                opLen = 1;
            }

            var paramName = clause.Substring(0, opIdx).Trim();
            var target = clause.Substring(opIdx + opLen).Trim();
            if (paramName.Length == 0 || target.Length == 0) continue;

            // Skip clauses whose LHS is a dependent-member path (e.g. `T.Element == Data?`);
            // they constrain associated types, not the parent param itself.
            if (paramName.Contains('.', StringComparison.Ordinal)) continue;

            if (!result.TryGetValue(paramName, out var list))
            {
                list = new List<(MethodConstraintKind, string)>();
                result[paramName] = list;
            }
            var entry = (kind, target);
            if (!list.Contains(entry))
                list.Add(entry);
        }
        return result;
    }

    private static Dictionary<string, List<ConcreteConformer>> LoadHints()
    {
        var result = new Dictionary<string, List<ConcreteConformer>>(StringComparer.Ordinal);

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("specialization-hints.json", StringComparison.Ordinal));

        if (resourceName == null) return result;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return result;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var file = JsonConvert.DeserializeObject<HintsFile>(json);
        if (file == null) return result;

        foreach (var protocol in file.Protocols)
        {
            var conformers = new List<ConcreteConformer>();
            foreach (var c in protocol.Conformers)
            {
                SwiftTypeName? typeName = null;
                try { typeName = SwiftTypeName.FromModuleQualifiedName(c.SwiftType); }
                catch (ArgumentException) { /* Generic types like Swift.Array<Swift.UInt8> */ }

                IReadOnlyDictionary<string, string>? associatedTypes = c.AssociatedTypes is { Count: > 0 }
                    ? new Dictionary<string, string>(c.AssociatedTypes, StringComparer.Ordinal)
                    : null;

                IReadOnlyList<string>? allowedModules = c.Modules is { Count: > 0 }
                    ? c.Modules.ToArray()
                    : null;

                conformers.Add(new ConcreteConformer(
                    c.SwiftType,
                    c.CSharpType,
                    typeName,
                    c.SwiftLiteral,
                    associatedTypes,
                    AvailabilityAnnotations: null,
                    AllowedModules: allowedModules));
            }
            result[protocol.Protocol] = conformers;
        }

        return result;
    }

    /// <summary>
    /// Exposes hint-based conformers for testing.
    /// </summary>
    internal static IReadOnlyDictionary<string, List<ConcreteConformer>> LoadedHints => _sharedHints.Value;

    /// <summary>
    /// Test-only view of the indexed module's plausibility-dependency set — the union
    /// of <see cref="ModuleDecl.Dependencies"/> and <see cref="ModuleDecl.DependencyModuleNames"/>
    /// captured during <see cref="IndexModuleConformances"/>. Exposed so tests can assert
    /// both lists are merged; production code reads the private field directly.
    /// </summary>
    internal IReadOnlySet<string> IndexedModuleDependenciesForTesting
        => _indexedModuleDependencies ?? (IReadOnlySet<string>)new HashSet<string>();
}
