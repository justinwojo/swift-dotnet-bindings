// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration;

/// <summary>
/// Centralized registry of Apple framework type knowledge.
/// Data is loaded from a single consolidated JSON file (apple-frameworks.json) embedded as a resource.
/// Two separate concerns:
/// 1. Module-level classification (auto-bridge, optional fallback, unsupported)
/// 2. Type-level knowledge (value types, name remapping, ObjC prefix detection)
/// </summary>
internal static class AppleFrameworkRegistry
{
    // --- Loaded Data ---

    private static readonly HashSet<string> _autoBridgeModules;
    // Modules that contain ZERO ObjC classes — every type they declare is a Swift
    // value type (struct/enum). `simd` is the canonical case: vectors, matrices, and
    // quaternions are all value types, so the broad autoBridge default ("unknown type
    // ⇒ ObjC class") is wrong for it. Listing this at module granularity makes the
    // classification robust to the module's full type surface (e.g. simd's integer and
    // packed vectors) instead of depending on an exhaustive hand-maintained valueTypes
    // allow-list — an omission there silently mis-bridges the type to a non-existent
    // `<module>.<Type>` wrapper class (CS0246).
    private static readonly HashSet<string> _valueTypesOnlyModules;
    private static readonly HashSet<string> _optionalFallbackModules;
    private static readonly HashSet<string> _concreteClassFallbackModules;
    private static readonly HashSet<string> _unsupportedModules;
    private static readonly HashSet<string> _wrapperImportableModules;
    private static readonly Dictionary<string, string> _moduleNamespaceRemaps;
    private static readonly Dictionary<string, string> _compileImportRemaps;
    // Reverse view of _compileImportRemaps: umbrella module → list of source modules.
    // Apple's `@_implementationOnly` re-exports collapse the canonical Swift type name
    // onto the umbrella module (e.g., RealityKit.Entity instead of RealityFoundation.Entity)
    // even though the type's real declaration lives in the source. The cross-module type
    // lookup consults this map so a name qualified with the umbrella module falls back to
    // the source module's TypeRecord. List-typed because in principle multiple sources
    // can re-export through one umbrella; the lookup probes them in registration order.
    private static readonly Dictionary<string, List<string>> _compileImportSourceModules;
    private static readonly Dictionary<string, string> _typeNameRemaps;
    private static readonly HashSet<string> _valueTypes;
    // The subset of _valueTypes whose entry additionally *describes* the shape: an integer-backed
    // NS_ENUM/NS_OPTIONS the Swift importer surfaces as a raw-value enum. Listing a name as a value
    // type only says "not an ObjC class", which withholds the synthetic bridged-class record and
    // leaves the name unresolvable — correct for shapes we cannot bind (Swift-only nested structs,
    // NSString-typedef constant groups, real classes with no .NET counterpart), but a needless loss
    // for a plain integer enum, which marshals as its raw value. A described name is the standing
    // claim that the *Swift* side is an integer enum — the one fact reflection over the .NET binding
    // cannot establish, because the bindings project NSString typed-constant groups as C# enums too.
    private static readonly HashSet<string> _integerEnumValueTypes;
    private static readonly Dictionary<ApplePlatform, HashSet<string>> _platformUnavailableModules;
    private static readonly string[] _objcPrefixes;
    // Per-module ObjC prefix tables. Modules that declare an explicit `objcPrefixes`
    // entry in apple-frameworks.json get a strict per-module prefix gate in
    // <see cref="IsObjCBridgedTypeName"/>; modules without a declared list fall back
    // to the original "all autoBridge class types are ObjC" behavior so we don't
    // accidentally re-classify real ObjC types whose modules haven't yet had their
    // prefix list backfilled (e.g. AppKit's NS-prefixed types).
    private static readonly Dictionary<string, string[]> _perModuleObjcPrefixes;
    private static readonly HashSet<string> _netUnavailableTypes;
    private static readonly Dictionary<string, string> _packageIds;
    // Union of every module name that appears in apple-frameworks.json, regardless of
    // which flags the entry sets. Used by callers that need to assert a module has a
    // registry entry at all (e.g. ObjCUsingsEmitter validates that every Apple-framework
    // using it might emit is registered, so a missing entry can't silently bypass the
    // IsModuleAvailableOnPlatform gate).
    private static readonly HashSet<string> _allKnownModules;

    // --- ObjC type-mapping tables (folded out of ObjCTypeMapper / StructsAndEnumsEmitter) ---
    // These were five+one hardcoded tables scattered across the ObjC emitter. They are now
    // a single schema-versioned sibling data file (objc-type-mappings.json) the registry owns,
    // so the "AppleFrameworkRegistry is the single source of truth" constraint actually holds
    // for ObjC type knowledge too.
    private static readonly Dictionary<string, string> _objcPointerTypeMappings;
    private static readonly Dictionary<string, string> _coreFoundationRefTypeMappings;
    private static readonly Dictionary<string, string> _objcPrimitiveTypeMappings;
    private static readonly (string ObjC, string Dotnet)[] _objcAcronymConventions;
    private static readonly HashSet<string> _objcValueTypes;
    private static readonly HashSet<string> _objcSystemStructs;

    // CGFloat is declared in CoreGraphics and re-exported through CoreFoundation, so the ABI
    // JSON surfaces both module-qualified spellings for the same scalar. This is a fixed Swift
    // overlay fact (not framework-list data), so it lives inline rather than in
    // apple-frameworks.json. Centralizing it lets the float-field / scalar-classification
    // special-cases read one predicate instead of re-listing both literals.
    private static readonly HashSet<string> _cgFloatAliases = new(StringComparer.Ordinal)
    {
        "CoreFoundation.CGFloat", "CoreGraphics.CGFloat",
    };

    /// <summary>
    /// Schema version this build of the registry understands for objc-type-mappings.json.
    /// Bump in lockstep with the data file's <c>schemaVersion</c> whenever the shape changes,
    /// mirroring the SwiftInterfaceParser kSchemaVersion/ExpectedSchemaVersion handshake.
    /// </summary>
    internal const int ExpectedObjCTypeMappingsSchemaVersion = 1;

    /// <summary>The one shape a <c>valueTypes</c> entry may describe today: an integer-backed enum.</summary>
    private const string IntegerEnumValueTypeKind = "enum";

    /// <summary>
    /// Classifies a <c>valueTypes</c> entry's declared shape: true when it describes an
    /// integer-backed enum, false when it describes nothing (the bare-name form, which keeps its
    /// historical "not an ObjC class" meaning and nothing more). An unrecognized shape throws
    /// rather than degrading to "described by nothing" — a typo would otherwise be indistinguishable
    /// from a deliberate bare entry and would silently leave the type unresolvable, which is the very
    /// failure a description exists to remove.
    /// </summary>
    internal static bool DescribesIntegerEnum(string qualifiedName, string? kind)
    {
        if (kind == null)
            return false;
        if (string.Equals(kind, IntegerEnumValueTypeKind, StringComparison.Ordinal))
            return true;

        throw new InvalidOperationException(
            $"apple-frameworks.json: value type '{qualifiedName}' declares unknown kind "
            + $"'{kind}'. The only describable shape is '{IntegerEnumValueTypeKind}' "
            + "(an integer-backed NS_ENUM/NS_OPTIONS).");
    }

    /// <summary>
    /// Reads a single <c>valueTypes</c> entry exactly as the registry loader does, so the
    /// accept/reject boundary for hand-authored entries is observable without rebuilding the
    /// embedded data file. Throws the same load-time exception a malformed entry raises in
    /// production.
    /// </summary>
    internal static (string Name, string? Kind) ParseValueTypeEntry(string entryJson)
    {
        var parsed = JsonConvert.DeserializeObject<ValueTypeDefinition>(entryJson)
            ?? throw new JsonSerializationException(
                "apple-frameworks.json: a valueTypes entry must not be null.");
        return (parsed.Name, parsed.Kind);
    }

    // --- JSON Model ---

    private sealed class FrameworkDefinitionsFile
    {
        [JsonProperty("frameworks")]
        public List<FrameworkDefinition> Frameworks { get; set; } = new();
    }

    private sealed class FrameworkDefinition
    {
        [JsonProperty("module")]
        public string Module { get; set; } = string.Empty;

        [JsonProperty("autoBridge")]
        public bool AutoBridge { get; set; }

        [JsonProperty("valueTypesOnly")]
        public bool ValueTypesOnly { get; set; }

        [JsonProperty("optionalFallback")]
        public bool OptionalFallback { get; set; }

        [JsonProperty("concreteClassFallback")]
        public bool ConcreteClassFallback { get; set; }

        [JsonProperty("unsupported")]
        public bool Unsupported { get; set; }

        [JsonProperty("wrapperImportable")]
        public bool WrapperImportable { get; set; }

        [JsonProperty("namespaceRemap")]
        public string? NamespaceRemap { get; set; }

        [JsonProperty("compileImportModule")]
        public string? CompileImportModule { get; set; }

        [JsonProperty("objcPrefixes")]
        public string[]? ObjcPrefixes { get; set; }

        [JsonProperty("platformUnavailable")]
        public string[]? PlatformUnavailable { get; set; }

        [JsonProperty("valueTypes")]
        public ValueTypeDefinition[]? ValueTypes { get; set; }

        [JsonProperty("typeRemaps")]
        public Dictionary<string, string>? TypeRemaps { get; set; }

        [JsonProperty("excludeFromXml")]
        public string[]? ExcludeFromXml { get; set; }

        [JsonProperty("netUnavailableTypes")]
        public string[]? NetUnavailableTypes { get; set; }

        [JsonProperty("packageId")]
        public string? PackageId { get; set; }
    }

    /// <summary>
    /// One <c>valueTypes</c> entry. Written either as a bare name — "this is not an ObjC class",
    /// with no claim about what it *is*, so it stays unresolvable — or as an object that also
    /// describes the shape (<c>{ "name": "PKPaymentButtonType", "kind": "enum" }</c>), which lets
    /// the resolver supply a real value-type record instead. The description is deliberately the
    /// narrow Swift-side fact only; the .NET identity (namespace, spelling, raw-value width,
    /// bitmask-ness) is read from the platform binding that actually ships, so it can never drift
    /// from what the emitted C# has to compile against.
    /// </summary>
    [JsonConverter(typeof(ValueTypeDefinitionConverter))]
    private sealed class ValueTypeDefinition
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Declared shape, or null when the entry only withholds ObjC bridging.</summary>
        public string? Kind { get; init; }
    }

    /// <summary>Reads a <c>valueTypes</c> entry written either as a bare string or as an object.</summary>
    private sealed class ValueTypeDefinitionConverter : JsonConverter<ValueTypeDefinition>
    {
        public override ValueTypeDefinition ReadJson(
            JsonReader reader, Type objectType, ValueTypeDefinition? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (token.Type == JTokenType.String)
            {
                var bare = token.Value<string>();
                if (string.IsNullOrEmpty(bare))
                    throw new JsonSerializationException(
                        "apple-frameworks.json: a valueTypes string entry must be a non-empty type name.");
                return new ValueTypeDefinition { Name = bare };
            }

            if (token is JObject obj)
            {
                var name = obj.Value<string>("name");
                if (string.IsNullOrEmpty(name))
                    throw new JsonSerializationException(
                        "apple-frameworks.json: a valueTypes object entry must carry a non-empty 'name'.");

                var unknown = obj.Properties()
                    .Select(p => p.Name)
                    .Where(p => !string.Equals(p, "name", StringComparison.Ordinal)
                             && !string.Equals(p, "kind", StringComparison.Ordinal))
                    .ToArray();
                if (unknown.Length > 0)
                    throw new JsonSerializationException(
                        $"apple-frameworks.json: valueTypes entry '{name}' carries unknown "
                        + $"propert{(unknown.Length == 1 ? "y" : "ies")} "
                        + $"'{string.Join("', '", unknown)}'. Only 'name' and 'kind' are read.");

                // The object form exists ONLY to describe the shape — the string form already says
                // "value type, shape undescribed". An object with no readable 'kind' is therefore an
                // authoring slip (a typo'd property name lands here), and silently degrading it to a
                // bare entry would quietly withhold the very record the entry was written to supply.
                var kind = obj.Value<string>("kind");
                if (string.IsNullOrEmpty(kind))
                    throw new JsonSerializationException(
                        $"apple-frameworks.json: valueTypes entry '{name}' is written in object form "
                        + "but declares no 'kind'. Use the plain string form for a value type whose "
                        + "shape is deliberately left undescribed.");

                return new ValueTypeDefinition { Name = name!, Kind = kind };
            }

            throw new JsonSerializationException(
                $"apple-frameworks.json: a valueTypes entry must be a string or an object, not {token.Type}.");
        }

        public override void WriteJson(JsonWriter writer, ValueTypeDefinition? value, JsonSerializer serializer)
            => throw new NotSupportedException("apple-frameworks.json is read-only registry data.");
    }

    private sealed class ObjCTypeMappingsFile
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("pointerTypeMappings")]
        public Dictionary<string, string> PointerTypeMappings { get; set; } = new();

        [JsonProperty("coreFoundationRefMappings")]
        public Dictionary<string, string> CoreFoundationRefMappings { get; set; } = new();

        [JsonProperty("primitiveTypeMappings")]
        public Dictionary<string, string> PrimitiveTypeMappings { get; set; } = new();

        [JsonProperty("acronymConventions")]
        public List<AcronymConventionEntry> AcronymConventions { get; set; } = new();

        [JsonProperty("objcValueTypes")]
        public List<string> ObjcValueTypes { get; set; } = new();

        [JsonProperty("systemStructs")]
        public List<string> SystemStructs { get; set; } = new();
    }

    private sealed class AcronymConventionEntry
    {
        [JsonProperty("objc")]
        public string ObjC { get; set; } = string.Empty;

        [JsonProperty("dotnet")]
        public string Dotnet { get; set; } = string.Empty;
    }

    // --- Static Constructor (loads from embedded JSON) ---

    static AppleFrameworkRegistry()
    {
        var objcTypeMappings = LoadObjCTypeMappings();
        _objcPointerTypeMappings = new Dictionary<string, string>(objcTypeMappings.PointerTypeMappings, StringComparer.Ordinal);
        _coreFoundationRefTypeMappings = new Dictionary<string, string>(objcTypeMappings.CoreFoundationRefMappings, StringComparer.Ordinal);
        _objcPrimitiveTypeMappings = new Dictionary<string, string>(objcTypeMappings.PrimitiveTypeMappings, StringComparer.Ordinal);
        _objcAcronymConventions = objcTypeMappings.AcronymConventions
            .Select(a => (a.ObjC, a.Dotnet))
            .ToArray();
        _objcValueTypes = new HashSet<string>(objcTypeMappings.ObjcValueTypes, StringComparer.Ordinal);
        _objcSystemStructs = new HashSet<string>(objcTypeMappings.SystemStructs, StringComparer.Ordinal);

        var definitions = LoadFrameworkDefinitions();

        _autoBridgeModules = new HashSet<string>(StringComparer.Ordinal);
        _valueTypesOnlyModules = new HashSet<string>(StringComparer.Ordinal);
        _optionalFallbackModules = new HashSet<string>(StringComparer.Ordinal);
        _concreteClassFallbackModules = new HashSet<string>(StringComparer.Ordinal);
        _unsupportedModules = new HashSet<string>(StringComparer.Ordinal);
        _wrapperImportableModules = new HashSet<string>(StringComparer.Ordinal);
        _moduleNamespaceRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _compileImportRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _compileImportSourceModules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        _typeNameRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _valueTypes = new HashSet<string>(StringComparer.Ordinal);
        _integerEnumValueTypes = new HashSet<string>(StringComparer.Ordinal);
        _netUnavailableTypes = new HashSet<string>(StringComparer.Ordinal);
        _packageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        _perModuleObjcPrefixes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        _allKnownModules = new HashSet<string>(StringComparer.Ordinal);
        var objcPrefixSet = new HashSet<string>(StringComparer.Ordinal);

        // Platform unavailable: build per-platform sets
        var tvOSUnavailable = new HashSet<string>(StringComparer.Ordinal);
        var macOSUnavailable = new HashSet<string>(StringComparer.Ordinal);
        var macCatalystUnavailable = new HashSet<string>(StringComparer.Ordinal);

        // Process definitions sorted by module name for deterministic loading
        foreach (var def in definitions.OrderBy(d => d.Module, StringComparer.Ordinal))
        {
            _allKnownModules.Add(def.Module);

            if (def.AutoBridge)
                _autoBridgeModules.Add(def.Module);

            if (def.ValueTypesOnly)
                _valueTypesOnlyModules.Add(def.Module);

            if (def.OptionalFallback)
                _optionalFallbackModules.Add(def.Module);

            if (def.ConcreteClassFallback)
                _concreteClassFallbackModules.Add(def.Module);

            if (def.Unsupported)
                _unsupportedModules.Add(def.Module);

            if (def.WrapperImportable)
                _wrapperImportableModules.Add(def.Module);

            if (def.NamespaceRemap != null)
                _moduleNamespaceRemaps[def.Module] = def.NamespaceRemap;

            if (!string.IsNullOrEmpty(def.CompileImportModule))
            {
                _compileImportRemaps[def.Module] = def.CompileImportModule!;
                if (!_compileImportSourceModules.TryGetValue(def.CompileImportModule!, out var sources))
                {
                    sources = new List<string>();
                    _compileImportSourceModules[def.CompileImportModule!] = sources;
                }
                sources.Add(def.Module);
            }

            if (def.ObjcPrefixes != null && def.ObjcPrefixes.Length > 0)
            {
                foreach (var prefix in def.ObjcPrefixes)
                    objcPrefixSet.Add(prefix);
                // Per-module prefix list (longest-first so multi-letter prefixes match
                // before sub-prefixes from the same module).
                _perModuleObjcPrefixes[def.Module] = def.ObjcPrefixes
                    .OrderByDescending(p => p.Length)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToArray();
            }

            if (def.PlatformUnavailable != null)
            {
                foreach (var platform in def.PlatformUnavailable)
                {
                    if (platform == "tvOS")
                        tvOSUnavailable.Add(def.Module);
                    else if (platform == "macOS")
                        macOSUnavailable.Add(def.Module);
                    else if (platform == "MacCatalyst")
                        macCatalystUnavailable.Add(def.Module);
                }
            }

            if (def.ValueTypes != null)
            {
                foreach (var vt in def.ValueTypes)
                {
                    var qualifiedName = $"{def.Module}.{vt.Name}";
                    _valueTypes.Add(qualifiedName);

                    if (DescribesIntegerEnum(qualifiedName, vt.Kind))
                        _integerEnumValueTypes.Add(qualifiedName);
                }
            }

            if (def.TypeRemaps != null)
            {
                foreach (var (swiftName, netName) in def.TypeRemaps)
                    _typeNameRemaps[$"{def.Module}.{swiftName}"] = netName;
            }

            if (def.NetUnavailableTypes != null)
            {
                foreach (var typeName in def.NetUnavailableTypes)
                    _netUnavailableTypes.Add($"{def.Module}.{typeName}");
            }

            if (!string.IsNullOrEmpty(def.PackageId))
                _packageIds[def.Module] = def.PackageId!;
        }

        // Sort prefixes by length descending for correct matching
        // (longer prefixes like "MTK" must be checked before "MT")
        _objcPrefixes = objcPrefixSet.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();

        _platformUnavailableModules = new Dictionary<ApplePlatform, HashSet<string>>();
        if (tvOSUnavailable.Count > 0)
            _platformUnavailableModules[ApplePlatform.tvOS] = tvOSUnavailable;
        if (macOSUnavailable.Count > 0)
            _platformUnavailableModules[ApplePlatform.macOS] = macOSUnavailable;
        // Mac Catalyst runs on the Mac, so frameworks the Mac lacks (OpenGL ES) are absent there
        // too — but Catalyst deliberately brings most of the iOS/UIKit family across, so a
        // macOS-unavailable framework is NOT automatically Catalyst-unavailable (UIKit is the
        // canonical counter-example). The annotation is therefore per-framework in the JSON, not
        // derived from the macOS set.
        if (macCatalystUnavailable.Count > 0)
            _platformUnavailableModules[ApplePlatform.MacCatalyst] = macCatalystUnavailable;
    }

    private static List<FrameworkDefinition> LoadFrameworkDefinitions()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Swift.Bindings.Data.apple-frameworks.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var file = JsonConvert.DeserializeObject<FrameworkDefinitionsFile>(json)
            ?? throw new InvalidOperationException("Failed to deserialize apple-frameworks.json.");

        return file.Frameworks;
    }

    private static ObjCTypeMappingsFile LoadObjCTypeMappings()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Swift.Bindings.Data.objc-type-mappings.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var file = JsonConvert.DeserializeObject<ObjCTypeMappingsFile>(json)
            ?? throw new InvalidOperationException("Failed to deserialize objc-type-mappings.json.");

        // Schema-version handshake: a producer/consumer shape change must bump both the data
        // file and ExpectedObjCTypeMappingsSchemaVersion in lockstep, so a stale embedded file
        // fails loud here instead of silently mis-mapping every ObjC type.
        if (file.SchemaVersion != ExpectedObjCTypeMappingsSchemaVersion)
            throw new InvalidOperationException(
                $"objc-type-mappings.json schemaVersion {file.SchemaVersion} does not match the "
                + $"expected version {ExpectedObjCTypeMappingsSchemaVersion}. Regenerate or bump both in lockstep.");

        return file;
    }

    // --- Public API (unchanged) ---

    /// <summary>Narrower set used by IsObjCModuleType to gate auto-bridging.</summary>
    public static bool IsAutoBridgeModule(string moduleName) => _autoBridgeModules.Contains(moduleName);

    /// <summary>Broader set used by Optional/Array element fallback.</summary>
    public static bool IsOptionalFallbackModule(string moduleName) => _optionalFallbackModules.Contains(moduleName);

    /// <summary>
    /// Modules that ship concrete Swift classes whose names do not always match
    /// an ObjC class prefix (e.g., RealityFoundation.Entity, RealityKit.AnchorEntity,
    /// SceneKit.ProgramNode). The @_cdecl-wrapper renderer uses this to recognise
    /// <c>Optional&lt;Class&gt;</c> on cross-module unresolved class names without
    /// requiring an XML database entry or ObjC prefix match. See
    /// <see cref="WrapperValidation.IsOptionalWithReferenceInner"/> Path 3.
    /// </summary>
    public static bool IsConcreteClassFallbackModule(string moduleName) => _concreteClassFallbackModules.Contains(moduleName);

    public static bool IsUnsupportedModule(string moduleName) => _unsupportedModules.Contains(moduleName);

    /// <summary>
    /// Centralizes the SkipReason classifier for unsupported-generic-constraint skips
    /// (callers pass the <c>Module</c> already surfaced by
    /// <see cref="GenericTypeEmitter.TryGetUnsupportedConstraint"/>). SwiftUI / SwiftUICore
    /// and Combine have dedicated <see cref="SkipReason"/> buckets that drive
    /// telemetry workaround recommendations; every other unsupported-constraint module
    /// falls into <see cref="SkipReason.UnsupportedType"/>. Single source of truth for
    /// the five emitters that previously inlined the same ternary classifier.
    /// </summary>
    public static SkipReason GetUnsupportedConstraintSkipReason(string moduleName) =>
        moduleName is "SwiftUI" or "SwiftUICore" ? SkipReason.SwiftUIConstraint
        : moduleName == "Combine" ? SkipReason.CombineFramework
        : SkipReason.UnsupportedType;

    /// <summary>
    /// Returns true when the wrapper Swift file should emit <c>import &lt;module&gt;</c> on
    /// account of this module's types appearing in the bound module's public surface.
    /// Opt-in per module via the <c>wrapperImportable</c> field in apple-frameworks.json so
    /// the predicate stays the single source of truth and ambient/SPI/umbrella-source
    /// modules (Network, _LocationEssentials, RealityFoundation) don't slip through.
    /// </summary>
    public static bool IsWrapperImportableModule(string moduleName) =>
        !string.IsNullOrEmpty(moduleName) && _wrapperImportableModules.Contains(moduleName);

    /// <summary>
    /// Returns true if a module is known to be available on the given platform.
    /// Returns true for unknown modules (conservative — only known-unavailable modules are gated).
    /// When platform is null, assumes iOS (all modules available).
    /// </summary>
    public static bool IsModuleAvailableOnPlatform(string moduleName, ApplePlatform? platform)
    {
        if (platform == null) return true;
        if (!_platformUnavailableModules.TryGetValue(platform.Value, out var excluded))
            return true;
        return !excluded.Contains(moduleName);
    }

    /// <summary>
    /// Returns true if a module has any entry in <c>apple-frameworks.json</c>, regardless
    /// of which flags it sets. Distinct from <see cref="IsModuleAvailableOnPlatform"/>'s
    /// conservative "unknown → available" fallback: callers that need to assert a name
    /// has been deliberately catalogued use this to fail loudly when a registry entry
    /// is missing (otherwise a typo or omission silently passes the availability gate).
    /// </summary>
    public static bool IsKnownModule(string moduleName) => _allKnownModules.Contains(moduleName);

    public static bool IsKnownValueType(string moduleQualifiedName)
    {
        if (_valueTypes.Contains(moduleQualifiedName))
            return true;

        // A valueTypesOnly module (e.g. simd) has no ObjC classes, so any type it
        // declares is a value type regardless of whether it was hand-listed in
        // valueTypes. Extract the leading module segment ("simd.simd_float2x2" → "simd";
        // "Foundation.NSAttributedString.Key" → "Foundation") and honor the module flag.
        var dotIndex = moduleQualifiedName.IndexOf('.');
        if (dotIndex > 0 &&
            _valueTypesOnlyModules.Contains(moduleQualifiedName.Substring(0, dotIndex)))
            return true;

        return false;
    }

    /// <summary>
    /// True when the registry describes this value type as an integer-backed enum — the Swift
    /// importer surfaces it as a raw-value enum, so it can carry a real value-type record instead
    /// of staying unresolvable. False for every bare <c>valueTypes</c> entry, which continues to
    /// mean "not an ObjC class" and nothing more. See <see cref="_integerEnumValueTypes"/>.
    /// </summary>
    public static bool IsIntegerEnumValueType(string moduleQualifiedName)
        => _integerEnumValueTypes.Contains(moduleQualifiedName);

    /// <summary>True when a module declares no ObjC classes — every type it exports is a
    /// Swift value type. See <see cref="_valueTypesOnlyModules"/>.</summary>
    public static bool IsValueTypesOnlyModule(string moduleName) => _valueTypesOnlyModules.Contains(moduleName);

    /// <summary>True for either module-qualified spelling of CoreGraphics' <c>CGFloat</c>
    /// (the type is re-exported through CoreFoundation, so the ABI JSON emits both
    /// <c>CoreGraphics.CGFloat</c> and <c>CoreFoundation.CGFloat</c>). The single source for the
    /// CGFloat scalar special-case so callers don't re-list both literals.</summary>
    public static bool IsCGFloat(string moduleQualifiedName) => _cgFloatAliases.Contains(moduleQualifiedName);

    // --- ObjC type-mapping queries (folded from ObjCTypeMapper / StructsAndEnumsEmitter) ---

    /// <summary>Maps a known ObjC pointer/object type name to its C# type (e.g. NSString → string).</summary>
    public static bool TryMapObjCPointerType(string objcName, out string csharpType) =>
        _objcPointerTypeMappings.TryGetValue(objcName, out csharpType!);

    /// <summary>Maps a CoreFoundation Ref typedef / opaque type to its C# type (e.g. CGImageRef → CGImage).</summary>
    public static bool TryMapCoreFoundationRefType(string objcName, out string csharpType) =>
        _coreFoundationRefTypeMappings.TryGetValue(objcName, out csharpType!);

    /// <summary>Maps a C/ObjC primitive type name to its C# type (e.g. BOOL → bool, NSInteger → nint).</summary>
    public static bool TryMapObjCPrimitiveType(string objcName, out string csharpType) =>
        _objcPrimitiveTypeMappings.TryGetValue(objcName, out csharpType!);

    /// <summary>True if the name is a known ObjC pointer/object type.</summary>
    public static bool IsObjCPointerType(string objcName) => _objcPointerTypeMappings.ContainsKey(objcName);

    /// <summary>True if the name is a known CoreFoundation Ref / opaque type.</summary>
    public static bool IsCoreFoundationRefType(string objcName) => _coreFoundationRefTypeMappings.ContainsKey(objcName);

    /// <summary>True if the name is a known C/ObjC primitive type.</summary>
    public static bool IsObjCPrimitiveType(string objcName) => _objcPrimitiveTypeMappings.ContainsKey(objcName);

    /// <summary>True if the name is a known Apple framework struct/value type (CGPoint, CMTime, simd_*, …).</summary>
    public static bool IsObjCValueType(string name) => _objcValueTypes.Contains(name);

    /// <summary>True if the struct is already defined by .NET MAUI's framework bindings and must not be re-emitted.</summary>
    public static bool IsObjCSystemStruct(string name) => _objcSystemStructs.Contains(name);

    /// <summary>Acronym casing pairs (objc → dotnet), ordered longer-first for correct substring replacement.</summary>
    public static IReadOnlyList<(string ObjC, string Dotnet)> ObjCAcronymConventions => _objcAcronymConventions;

    /// <summary>All C# type names the ObjC pointer table can produce. Used to seed known-type sets.</summary>
    public static IEnumerable<string> ObjCPointerTypeMappedValues => _objcPointerTypeMappings.Values;

    /// <summary>All C# type names the CoreFoundation Ref table can produce.</summary>
    public static IEnumerable<string> CoreFoundationRefTypeMappedValues => _coreFoundationRefTypeMappings.Values;

    /// <summary>All C# type names the primitive table can produce.</summary>
    public static IEnumerable<string> ObjCPrimitiveTypeMappedValues => _objcPrimitiveTypeMappings.Values;

    /// <summary>All known Apple framework value-type names.</summary>
    public static IEnumerable<string> ObjCValueTypeNames => _objcValueTypes;

    /// <summary>
    /// True once the folded ObjC type-mapping tables are loaded and non-empty. Consumers
    /// (ObjCTypeMapper) startup-assert on this so a failed embed/load fails loud rather than
    /// silently mis-mapping every ObjC type to a passthrough name.
    /// </summary>
    public static bool HasObjCTypeMappings =>
        _objcPointerTypeMappings.Count > 0
        && _coreFoundationRefTypeMappings.Count > 0
        && _objcPrimitiveTypeMappings.Count > 0
        && _objcAcronymConventions.Length > 0
        && _objcValueTypes.Count > 0
        && _objcSystemStructs.Count > 0;

    /// <summary>Module-level only remapping (ObjectiveC→Foundation, QuartzCore→CoreAnimation, etc.)</summary>
    public static string MapModuleToNetNamespace(string swiftModule)
    {
        if (string.IsNullOrEmpty(swiftModule)) return swiftModule;
        return _moduleNamespaceRemaps.TryGetValue(swiftModule, out var mapped) ? mapped : swiftModule;
    }

    /// <summary>
    /// Resolves the .NET namespace that owns an Apple SDK type from the resolved header path clang
    /// reported for its declaration. Apple SDK class/protocol headers live at
    /// <c>…/&lt;Framework&gt;.framework/Headers/…</c>; the <c>&lt;Framework&gt;</c> segment is the
    /// authoritative owning framework (ground truth — not unsound name-prefix inference, where e.g.
    /// <c>CM</c> is ambiguous between CoreMedia and CoreMotion), which is then run through the
    /// module→.NET-namespace remap (<see cref="MapModuleToNetNamespace"/>, e.g.
    /// <c>QuartzCore</c>→<c>CoreAnimation</c>). Returns false for paths with no <c>.framework</c>
    /// segment (e.g. <c>/usr/include/…</c> runtime headers), whose types carry no derivable namespace.
    /// </summary>
    public static bool TryResolveFrameworkNamespaceFromHeaderPath(string? headerPath, out string netNamespace)
    {
        netNamespace = "";
        if (string.IsNullOrEmpty(headerPath)) return false;

        const string marker = ".framework/";
        var normalized = headerPath.Replace('\\', '/');
        // Use the LAST .framework segment: for an embedded framework
        // (A.framework/Frameworks/B.framework/Headers/X.h) the immediately-owning framework is B.
        var markerIdx = normalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIdx < 0) return false;

        var segStart = normalized.LastIndexOf('/', markerIdx - 1) + 1; // 0 when no preceding slash
        var framework = normalized.Substring(segStart, markerIdx - segStart);
        if (framework.Length == 0) return false;

        netNamespace = MapModuleToNetNamespace(framework);
        return !string.IsNullOrEmpty(netNamespace);
    }

    /// <summary>
    /// Returns the umbrella module to write on the wrapper Swift's <c>import</c> line
    /// for a Swift module that Apple has marked <c>@_implementationOnly</c> (e.g.,
    /// <c>RealityFoundation</c> → <c>RealityKit</c>). Returns the input unchanged when
    /// no remap is registered. Type qualifications and the .NET namespace continue to
    /// use the original module name — only the literal import line is rewritten.
    /// </summary>
    public static string MapModuleToCompileImport(string swiftModule)
    {
        if (string.IsNullOrEmpty(swiftModule)) return swiftModule;
        return _compileImportRemaps.TryGetValue(swiftModule, out var mapped) ? mapped : swiftModule;
    }

    /// <summary>
    /// Reverse direction of <see cref="MapModuleToCompileImport"/>: given an umbrella module
    /// (e.g., <c>RealityKit</c>), returns the source modules that Apple has marked
    /// <c>@_implementationOnly</c> through it (e.g., <c>RealityFoundation</c>). Used by the
    /// type database's cross-module lookup so a type name qualified with the umbrella name
    /// falls back to its real declaring module's TypeRecord — without this, references like
    /// <c>RealityKit.Entity</c> in RealityFoundation's own ABI JSON (canonical Swift name uses
    /// the umbrella because of <c>@_implementationOnly</c>) cannot find Entity's record and
    /// the projection factory drops to <c>SwiftOptional&lt;IntPtr&gt;</c> for nullable cases.
    /// Returns an empty list when no source modules are registered.
    /// </summary>
    public static IReadOnlyList<string> GetCompileImportSourceModules(string umbrellaModule)
    {
        if (string.IsNullOrEmpty(umbrellaModule)) return Array.Empty<string>();
        return _compileImportSourceModules.TryGetValue(umbrellaModule, out var sources)
            ? sources
            : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Replaces all known Swift module name prefixes with their .NET namespace equivalents in a string.
    /// </summary>
    public static string MapModulesInString(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (swiftModule, netNamespace) in _moduleNamespaceRemaps)
        {
            var swiftPrefix = $"{swiftModule}.";
            if (text.Contains(swiftPrefix, StringComparison.Ordinal))
                text = text.Replace(swiftPrefix, $"{netNamespace}.", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// Rewrites a leading underscore-prefixed (SPI) Swift module prefix to its public
    /// counterpart using <c>namespaceRemap</c> from apple-frameworks.json.
    /// Example: <c>_LocationEssentials.CLLocation</c> → <c>CoreLocation.CLLocation</c>.
    /// Non-SPI module prefixes are left alone (unlike <see cref="MapModulesInString"/>,
    /// which rewrites every registered remap).
    /// </summary>
    public static string RewriteSpiModulePrefix(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var dot = text.IndexOf('.');
        if (dot <= 0) return text;
        var module = text.Substring(0, dot);
        if (!module.StartsWith("_", StringComparison.Ordinal)) return text;
        if (!_moduleNamespaceRemaps.TryGetValue(module, out var mapped)) return text;
        return mapped + text.Substring(dot);
    }

    /// <summary>
    /// Returns true if the given module name is an SPI (underscore-prefixed) Swift module
    /// that has a public counterpart registered in apple-frameworks.json.
    /// </summary>
    public static bool TryMapSpiModuleToPublic(string swiftModule, out string publicModule)
    {
        publicModule = swiftModule;
        if (string.IsNullOrEmpty(swiftModule) || !swiftModule.StartsWith("_", StringComparison.Ordinal))
            return false;
        if (!_moduleNamespaceRemaps.TryGetValue(swiftModule, out var mapped))
            return false;
        publicModule = mapped;
        return true;
    }

    /// <summary>
    /// Full type name remapping for string-only callers.
    /// Checks explicit type remappings.
    /// </summary>
    public static bool TryGetNetTypeName(string moduleQualifiedSwiftName, out string netName)
    {
        if (_typeNameRemaps.TryGetValue(moduleQualifiedSwiftName, out netName!))
            return true;
        netName = default!;
        return false;
    }

    /// <summary>
    /// Returns true if the type name portion of a module-qualified name starts with
    /// a known ObjC class prefix followed by an uppercase letter.
    /// </summary>
    public static bool HasObjCClassPrefix(string moduleQualifiedName)
    {
        var dotIndex = moduleQualifiedName.IndexOf('.');
        if (dotIndex < 0 || dotIndex >= moduleQualifiedName.Length - 1)
            return false;

        var typeName = moduleQualifiedName.AsSpan(dotIndex + 1);

        foreach (var prefix in _objcPrefixes)
        {
            if (typeName.Length > prefix.Length &&
                typeName.StartsWith(prefix.AsSpan(), StringComparison.Ordinal) &&
                char.IsUpper(typeName[prefix.Length]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the bare type name starts with one of the known Apple ObjC class
    /// prefixes (loaded from apple-frameworks.json) followed by an uppercase letter.
    /// Used by the ObjC binding generator to distinguish Apple SDK types (which the
    /// .NET iOS bindings provide) from third-party types (which need their own bindings)
    /// when the AST itself doesn't carry source-of-origin information — e.g. under
    /// <c>-fmodules</c> where SDK declarations are not expanded into the parsed AST.
    /// </summary>
    public static bool TypeNameStartsWithKnownObjCPrefix(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        var span = typeName.AsSpan();
        foreach (var prefix in _objcPrefixes)
        {
            if (span.Length > prefix.Length &&
                span.StartsWith(prefix.AsSpan(), StringComparison.Ordinal) &&
                char.IsUpper(span[prefix.Length]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true when the module-qualified name should be treated as an ObjC-bridged
    /// class type for marshalling and existential filtering. Three-tier logic:
    ///
    /// 1. The module must be in the auto-bridge set, and the name must not be in the
    ///    explicit value-type exclusion set.
    /// 2. An explicit <c>typeRemaps</c> entry (Swift→.NET ObjC name) is the strongest
    ///    "this is ObjC" signal — Apple's Foundation overlay drops the <c>NS</c> prefix
    ///    on many classes (<c>Foundation.URLSession</c>, <c>Foundation.Bundle</c>), so
    ///    a name that doesn't match the module's prefix can still be ObjC by intent.
    /// 3. If the module declares <c>objcPrefixes</c>, the type's bare name MUST start
    ///    with one of THIS module's declared prefixes followed by an uppercase letter.
    ///    Swift-only types whose names don't match return false — this is what closes
    ///    the existential-filter bug for cases like
    ///    <c>RealityKit.SynchronizationPeerID</c> (no <c>RE</c> prefix).
    /// 4. If the module declares no <c>objcPrefixes</c>, the answer falls back to true
    ///    (preserves the original behavior for modules whose prefix list hasn't been
    ///    populated yet — e.g. AppKit, AVFAudio — so we don't accidentally re-classify
    ///    real ObjC types).
    ///
    /// This is the single source of truth shared by <see cref="TypeDatabaseExtensions.IsObjCModuleType"/>
    /// and <see cref="TypeDatabaseExtensions.IsObjCClassSwiftType"/>.
    /// </summary>
    public static bool IsObjCBridgedTypeName(string moduleName, string moduleQualifiedName)
    {
        if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(moduleQualifiedName))
            return false;
        if (!_autoBridgeModules.Contains(moduleName))
            return false;
        if (_valueTypesOnlyModules.Contains(moduleName) || _valueTypes.Contains(moduleQualifiedName))
            return false;

        // Explicit Swift→.NET typeRemaps entry — Apple's Foundation overlay drops NS
        // prefixes (URLSession → NSURLSession, Bundle → NSBundle, ...) so a remapped
        // Swift name is intentionally bridging to ObjC even without a matching prefix.
        if (_typeNameRemaps.ContainsKey(moduleQualifiedName))
            return true;

        if (!_perModuleObjcPrefixes.TryGetValue(moduleName, out var prefixes))
            return true;

        // Strict per-module prefix gate. Strip the module portion (if present) before
        // matching so the prefix check operates on the bare type-name span. Nested
        // names like "ARRaycastQuery.Target" still anchor to the head segment.
        var typeName = moduleQualifiedName.AsSpan();
        var dotIndex = typeName.IndexOf('.');
        if (dotIndex >= 0 && moduleQualifiedName.AsSpan(0, dotIndex).SequenceEqual(moduleName.AsSpan()))
            typeName = typeName.Slice(dotIndex + 1);

        foreach (var prefix in prefixes)
        {
            if (typeName.Length > prefix.Length &&
                typeName.StartsWith(prefix.AsSpan(), StringComparison.Ordinal) &&
                char.IsUpper(typeName[prefix.Length]))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsPointerType(string name) =>
        name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";

    /// <summary>
    /// Returns true if the module-qualified name represents a nested type
    /// (e.g., "Foundation.NSAttributedString.Key" has two dots).
    /// </summary>
    public static bool IsNestedType(string moduleQualifiedName)
    {
        var firstDot = moduleQualifiedName.IndexOf('.');
        if (firstDot < 0) return false;
        return moduleQualifiedName.IndexOf('.', firstDot + 1) >= 0;
    }

    public static bool IsKnownObjCRootClass(string name) => name is "NSObject" or "NSProxy";

    /// <summary>
    /// Returns true if the given module-qualified type name is an auto-bridged Swift type
    /// that does NOT exist in the corresponding .NET assembly. Members referencing these
    /// types cause CS0234 at compile time, so the generator suppresses them. Data is sourced
    /// from <c>apple-frameworks.json</c>'s <c>netUnavailableTypes</c> entry per module — add
    /// new exclusions there, not in code.
    /// </summary>
    public static bool IsNetUnavailableType(string moduleQualifiedName) =>
        _netUnavailableTypes.Contains(moduleQualifiedName);

    /// <summary>
    /// Returns the NuGet package ID for a Swift module if one is registered in
    /// <c>apple-frameworks.json</c>'s <c>packageId</c> field. Used by apple-framework-mode
    /// auto-injection of cross-module dependency edges (e.g.,
    /// <c>RealityKit</c> → <c>SwiftBindings.Apple.RealityKit</c>).
    /// Returns false for any module without a registered packageId — including marker
    /// imports like <c>Swift</c>, <c>_Concurrency</c>, <c>simd</c>, and Apple SDK modules
    /// that do not ship as standalone binding packages.
    /// </summary>
    public static bool TryGetPackageId(string moduleName, out string packageId)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            packageId = string.Empty;
            return false;
        }
        return _packageIds.TryGetValue(moduleName, out packageId!);
    }

    /// <summary>
    /// Wrapper-import suppression gate. Returns true if the wrapper Swift source should
    /// skip re-emitting a declared sibling `import X` line because the umbrella chain
    /// (or already-emitted surface-driven imports) covers it. Covers: Swift stdlib /
    /// ObjC runtime modules + apple-frameworks.json's <c>autoBridge</c>,
    /// <c>optionalFallback</c>, <c>concreteClassFallback</c>, and <c>unsupported</c>
    /// sets. Modules whose only flag is <c>wrapperImportable</c> (CoreGraphics, CryptoKit,
    /// SwiftUI, etc.) are NOT here — that field drives the *positive* "may emit
    /// `import X`" whitelist via <see cref="IsWrapperImportableModule"/>, which is
    /// a separate, opposite gate.
    /// </summary>
    public static bool ShouldSuppressDeclaredWrapperImport(string moduleName)
    {
        if (IsSwiftSystemModule(moduleName))
            return true;

        return _autoBridgeModules.Contains(moduleName) ||
               _optionalFallbackModules.Contains(moduleName) ||
               _concreteClassFallbackModules.Contains(moduleName) ||
               _unsupportedModules.Contains(moduleName);
    }

    /// <summary>
    /// Parser re-export keep-list. Returns true if a foreign-module TypeDecl with no
    /// current-module extension children should still be kept (with a moduleName override)
    /// rather than skipped as a pure re-export. Deliberately narrower than
    /// <see cref="ShouldSuppressDeclaredWrapperImport"/>: excludes
    /// <c>concreteClassFallback</c>-only modules (RealityFoundation, etc.) so cross-module
    /// extensions whose receiver lives in such a module route through the children-first
    /// branch in <c>SwiftABIParser.HandleTypeDecl</c> instead of being processed as
    /// a system re-export with the wrong namespace qualification.
    /// </summary>
    public static bool IsSystemReexportAllowedModule(string moduleName)
    {
        if (IsSwiftSystemModule(moduleName))
            return true;

        return _autoBridgeModules.Contains(moduleName) ||
               _optionalFallbackModules.Contains(moduleName) ||
               _unsupportedModules.Contains(moduleName);
    }

    private static bool IsSwiftSystemModule(string moduleName) =>
        moduleName is "Swift" or "_Concurrency" or "_StringProcessing" or
                      "__ObjC" or "Dispatch" or "CoreFoundation" or "ObjectiveC" or "Security";
}
