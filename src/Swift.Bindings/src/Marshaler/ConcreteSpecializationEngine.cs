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
        IReadOnlyList<AvailabilityAnnotation>? AvailabilityAnnotations = null);

    /// <summary>
    /// A method that can be specialized, along with its specialization info.
    /// </summary>
    public record SpecializableMethod(
        MethodDecl Method,
        List<SpecializableParam> SpecializableParams);

    /// <summary>
    /// A generic parameter that can be concretely specialized.
    /// </summary>
    public record SpecializableParam(
        GenericArgumentDecl GenericParam,
        SwiftTypeName ConstraintProtocol,
        List<ConcreteConformer> Conformers);

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
    }

    // --- Shared hint data (loaded once) ---
    private static readonly Lazy<Dictionary<string, List<ConcreteConformer>>> _sharedHints =
        new(LoadHints);

    public ConcreteSpecializationEngine(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _hintConformers = _sharedHints.Value;
        _abiConformers = new Dictionary<string, List<ConcreteConformer>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Indexes module-local conformances from a ModuleDecl's type declarations.
    /// Call once per module before querying specializable methods.
    /// </summary>
    public void IndexModuleConformances(ModuleDecl moduleDecl)
    {
        foreach (var typeDecl in moduleDecl.Types)
            IndexTypeConformances(typeDecl);
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
    /// Checks whether the specialization hints registry has conformers for the given
    /// module-qualified protocol name (e.g., "MusicKit.MusicCatalogSearchable"). Hint-only —
    /// does not consider ABI-discovered conformers, since those require an engine instance.
    /// Used by validator paths that need a stateless conformer check.
    /// </summary>
    public static bool HasKnownHintConformers(string protocolQualifiedName) =>
        _sharedHints.Value.TryGetValue(protocolQualifiedName, out var list) && list.Count > 0;

    /// <summary>
    /// Returns hint-registered conformers for a protocol, or an empty list if none exist.
    /// Stateless accessor — does not consider ABI-discovered conformers.
    /// </summary>
    public static IReadOnlyList<ConcreteConformer> GetHintConformers(string protocolQualifiedName) =>
        _sharedHints.Value.TryGetValue(protocolQualifiedName, out var list)
            ? list
            : Array.Empty<ConcreteConformer>();

    /// <summary>
    /// Returns known concrete conformers for a protocol, combining hints and ABI sources.
    /// </summary>
    public List<ConcreteConformer> GetConformers(SwiftTypeName protocolName)
    {
        var result = new List<ConcreteConformer>();

        var key = protocolName.ToString();

        if (_hintConformers.TryGetValue(key, out var hintList))
            result.AddRange(hintList);

        if (_abiConformers.TryGetValue(key, out var abiList))
        {
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

    /// <summary>
    /// Finds methods on a type that can be concretely specialized.
    /// A method is specializable if it has method-own generic parameters constrained
    /// to protocols with associated types or Self requirements, AND all such protocols
    /// have at least one known conformer.
    /// </summary>
    public List<SpecializableMethod> FindSpecializableMethods(TypeDecl typeDecl)
    {
        var result = new List<SpecializableMethod>();

        // Collect parent type generic parameter names
        var parentParamNames = typeDecl.IsGeneric
            ? new HashSet<string>(typeDecl.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        foreach (var method in typeDecl.Methods)
        {
            if (!method.IsGeneric) continue;

            // Find method-own generic params (not inherited from parent type)
            var ownParams = method.GenericParameters
                .Where(p => !parentParamNames.Contains(p.TypeName))
                .ToList();

            if (ownParams.Count == 0) continue;

            var specializableParams = new List<SpecializableParam>();

            foreach (var param in ownParams)
            {
                // Collect same-type constraints on the param's associated types
                // (e.g., τ_0_0.Element == Swift.String). Conformers must satisfy these
                // via their declared AssociatedTypes map, or we can't specialize safely.
                var associatedConstraints = param.AssosiatedTypeConformances
                    .Where(c => c.Kind == ConformanceKind.ConcreteType && c.Path.Length >= 2)
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

                // Filter conformers to those whose types are resolvable
                var usableConformers = conformers
                    .Where(c => c.CSharpType != null && !IsCSharpPrimitiveType(c.CSharpType))
                    .Where(c => ConformerSatisfiesAssociatedTypes(c, associatedConstraints))
                    .ToList();

                if (usableConformers.Count == 0) continue;

                specializableParams.Add(new SpecializableParam(param, protocolConstraint, usableConformers));
            }

            // Only specializable if at least one param has conformers
            if (specializableParams.Count > 0)
            {
                result.Add(new SpecializableMethod(method, specializableParams));
            }
        }

        return result;
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
        // Array conformers (e.g., byte[]) are rejected here: the specialization emitter
        // calls `.Payload.DangerousGetHandle()` on generic-param arguments, which doesn't
        // exist on C# arrays. Until array marshalling is wired through the bridge, treat
        // arrays as unspecializable so the hint entry is effectively a no-op.
        if (typeName.EndsWith("[]", StringComparison.Ordinal)) return true;

        return typeName switch
        {
            "int" or "uint" or "long" or "ulong" or "short" or "ushort" or
            "byte" or "sbyte" or "float" or "double" or "bool" or "char" or
            "nint" or "nuint" or "decimal" or "string" => true,
            _ => false
        };
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

                conformers.Add(new ConcreteConformer(
                    c.SwiftType,
                    c.CSharpType,
                    typeName,
                    c.SwiftLiteral,
                    associatedTypes));
            }
            result[protocol.Protocol] = conformers;
        }

        return result;
    }

    /// <summary>
    /// Exposes hint-based conformers for testing.
    /// </summary>
    internal static IReadOnlyDictionary<string, List<ConcreteConformer>> LoadedHints => _sharedHints.Value;
}
