// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Newtonsoft.Json;

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
    private static readonly HashSet<string> _optionalFallbackModules;
    private static readonly HashSet<string> _unsupportedModules;
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
    private static readonly Dictionary<ApplePlatform, HashSet<string>> _platformUnavailableModules;
    private static readonly string[] _objcPrefixes;
    // Per-module ObjC prefix tables. Modules that declare an explicit `objcPrefixes`
    // entry in apple-frameworks.json get a strict per-module prefix gate in
    // <see cref="IsObjCBridgedTypeName"/>; modules without a declared list fall back
    // to the original "all autoBridge class types are ObjC" behavior so we don't
    // accidentally re-classify real ObjC types whose modules haven't yet had their
    // prefix list backfilled (e.g. AppKit's NS-prefixed types).
    private static readonly Dictionary<string, string[]> _perModuleObjcPrefixes;
    private static readonly HashSet<string> _knownModulesForElements;
    private static readonly HashSet<string> _netUnavailableTypes;

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

        [JsonProperty("optionalFallback")]
        public bool OptionalFallback { get; set; }

        [JsonProperty("unsupported")]
        public bool Unsupported { get; set; }

        [JsonProperty("namespaceRemap")]
        public string? NamespaceRemap { get; set; }

        [JsonProperty("compileImportModule")]
        public string? CompileImportModule { get; set; }

        [JsonProperty("objcPrefixes")]
        public string[]? ObjcPrefixes { get; set; }

        [JsonProperty("platformUnavailable")]
        public string[]? PlatformUnavailable { get; set; }

        [JsonProperty("knownModuleForElements")]
        public bool KnownModuleForElements { get; set; }

        [JsonProperty("valueTypes")]
        public string[]? ValueTypes { get; set; }

        [JsonProperty("typeRemaps")]
        public Dictionary<string, string>? TypeRemaps { get; set; }

        [JsonProperty("excludeFromXml")]
        public string[]? ExcludeFromXml { get; set; }

        [JsonProperty("netUnavailableTypes")]
        public string[]? NetUnavailableTypes { get; set; }
    }

    // --- Static Constructor (loads from embedded JSON) ---

    static AppleFrameworkRegistry()
    {
        var definitions = LoadFrameworkDefinitions();

        _autoBridgeModules = new HashSet<string>(StringComparer.Ordinal);
        _optionalFallbackModules = new HashSet<string>(StringComparer.Ordinal);
        _unsupportedModules = new HashSet<string>(StringComparer.Ordinal);
        _moduleNamespaceRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _compileImportRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _compileImportSourceModules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        _typeNameRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _valueTypes = new HashSet<string>(StringComparer.Ordinal);
        _knownModulesForElements = new HashSet<string>(StringComparer.Ordinal);
        _netUnavailableTypes = new HashSet<string>(StringComparer.Ordinal);
        _perModuleObjcPrefixes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var objcPrefixSet = new HashSet<string>(StringComparer.Ordinal);

        // Platform unavailable: build per-platform sets
        var tvOSUnavailable = new HashSet<string>(StringComparer.Ordinal);
        var macOSUnavailable = new HashSet<string>(StringComparer.Ordinal);

        // Process definitions sorted by module name for deterministic loading
        foreach (var def in definitions.OrderBy(d => d.Module, StringComparer.Ordinal))
        {
            if (def.AutoBridge)
                _autoBridgeModules.Add(def.Module);

            if (def.OptionalFallback)
                _optionalFallbackModules.Add(def.Module);

            if (def.Unsupported)
                _unsupportedModules.Add(def.Module);

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

            if (def.KnownModuleForElements)
                _knownModulesForElements.Add(def.Module);

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
                }
            }

            if (def.ValueTypes != null)
            {
                foreach (var vt in def.ValueTypes)
                    _valueTypes.Add($"{def.Module}.{vt}");
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
        }

        // Sort prefixes by length descending for correct matching
        // (longer prefixes like "MTK" must be checked before "MT")
        _objcPrefixes = objcPrefixSet.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();

        _platformUnavailableModules = new Dictionary<ApplePlatform, HashSet<string>>();
        if (tvOSUnavailable.Count > 0)
            _platformUnavailableModules[ApplePlatform.tvOS] = tvOSUnavailable;
        if (macOSUnavailable.Count > 0)
            _platformUnavailableModules[ApplePlatform.macOS] = macOSUnavailable;
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

    // --- Public API (unchanged) ---

    /// <summary>Narrower set used by IsObjCModuleType to gate auto-bridging.</summary>
    public static bool IsAutoBridgeModule(string moduleName) => _autoBridgeModules.Contains(moduleName);

    /// <summary>Broader set used by Optional/Array element fallback.</summary>
    public static bool IsOptionalFallbackModule(string moduleName) => _optionalFallbackModules.Contains(moduleName);

    public static bool IsUnsupportedModule(string moduleName) => _unsupportedModules.Contains(moduleName);

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

    public static bool IsKnownValueType(string moduleQualifiedName) => _valueTypes.Contains(moduleQualifiedName);

    /// <summary>Module-level only remapping (ObjectiveC→Foundation, QuartzCore→CoreAnimation, etc.)</summary>
    public static string MapModuleToNetNamespace(string swiftModule)
    {
        if (string.IsNullOrEmpty(swiftModule)) return swiftModule;
        return _moduleNamespaceRemaps.TryGetValue(swiftModule, out var mapped) ? mapped : swiftModule;
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
        if (_valueTypes.Contains(moduleQualifiedName))
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

    public static bool IsKnownModuleForElements(string moduleName) =>
        _knownModulesForElements.Contains(moduleName);

    /// <summary>
    /// Returns true if the module is a known Apple framework or Swift system module.
    /// Used by the parser to distinguish system re-exports (allowed) from third-party
    /// re-exports (should be skipped).
    /// </summary>
    public static bool IsKnownAppleOrSystemModule(string moduleName)
    {
        // Swift standard library and runtime modules not in apple-frameworks.json
        if (moduleName is "Swift" or "_Concurrency" or "_StringProcessing" or
            "__ObjC" or "Dispatch" or "CoreFoundation" or "ObjectiveC" or "Security")
            return true;

        // Check all apple-frameworks.json module sets
        return _autoBridgeModules.Contains(moduleName) ||
               _optionalFallbackModules.Contains(moduleName) ||
               _unsupportedModules.Contains(moduleName);
    }
}
