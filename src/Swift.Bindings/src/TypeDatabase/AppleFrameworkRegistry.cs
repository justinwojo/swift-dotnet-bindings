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
    private static readonly Dictionary<string, string> _typeNameRemaps;
    private static readonly HashSet<string> _valueTypes;
    private static readonly Dictionary<ApplePlatform, HashSet<string>> _platformUnavailableModules;
    private static readonly string[] _objcPrefixes;
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
        _typeNameRemaps = new Dictionary<string, string>(StringComparer.Ordinal);
        _valueTypes = new HashSet<string>(StringComparer.Ordinal);
        _knownModulesForElements = new HashSet<string>(StringComparer.Ordinal);
        _netUnavailableTypes = new HashSet<string>(StringComparer.Ordinal);
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

            if (def.KnownModuleForElements)
                _knownModulesForElements.Add(def.Module);

            if (def.ObjcPrefixes != null)
            {
                foreach (var prefix in def.ObjcPrefixes)
                    objcPrefixSet.Add(prefix);
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
