// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Root model for a bridge-hints.json sidecar file.
/// Allows users to override SwiftUI bridge auto-detection for specific views.
/// </summary>
public class BridgeHintsFile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("views")]
    public Dictionary<string, ViewHint>? Views { get; set; }

    [JsonPropertyName("globalSettings")]
    public GlobalSettingsHint? GlobalSettings { get; set; }
}

/// <summary>
/// Per-view hint overrides for bridge generation.
/// </summary>
public class ViewHint
{
    [JsonPropertyName("skip")]
    public bool? Skip { get; set; }

    [JsonPropertyName("forceTemplate")]
    public bool? ForceTemplate { get; set; }

    [JsonPropertyName("preferredInit")]
    public int? PreferredInit { get; set; }

    [JsonPropertyName("asyncPattern")]
    public AsyncPatternHint? AsyncPattern { get; set; }

    [JsonPropertyName("parameterOverrides")]
    public Dictionary<string, ParameterOverrideHint>? ParameterOverrides { get; set; }

    [JsonPropertyName("extraSwiftImports")]
    public List<string>? ExtraSwiftImports { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Hint for forcing async classification on a view.
/// The dependencyChain and resultMonitor fields are deserialized for forward compatibility
/// but not consumed for emission in Phase 3.
/// </summary>
public class AsyncPatternHint
{
    [JsonPropertyName("dependencyChain")]
    public List<DependencyChainStep>? DependencyChain { get; set; }

    [JsonPropertyName("resultMonitor")]
    public ResultMonitorHint? ResultMonitor { get; set; }
}

/// <summary>
/// A step in a manually specified dependency chain (forward compatibility — not consumed in Phase 3).
/// </summary>
public class DependencyChainStep
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("factory")]
    public string? Factory { get; set; }

    [JsonPropertyName("params")]
    public Dictionary<string, string>? Params { get; set; }
}

/// <summary>
/// Result monitor configuration (forward compatibility — not consumed in Phase 3).
/// </summary>
public class ResultMonitorHint
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }
}

/// <summary>
/// Parameter override hint (forward compatibility — not consumed in Phase 3).
/// </summary>
public class ParameterOverrideHint
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

/// <summary>
/// Global settings for bridge generation.
/// </summary>
public class GlobalSettingsHint
{
    [JsonPropertyName("maxAsyncChainDepth")]
    public int? MaxAsyncChainDepth { get; set; }

    [JsonPropertyName("maxClosureParams")]
    public int? MaxClosureParams { get; set; }

    [JsonPropertyName("extraSwiftImports")]
    public List<string>? ExtraSwiftImports { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible deserialization.
/// </summary>
[JsonSerializable(typeof(BridgeHintsFile))]
internal partial class BridgeHintsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads and validates bridge hints from JSON sidecar files.
/// </summary>
public static class BridgeHintsLoader
{
    private static readonly HashSet<string> KnownRootKeys = new(StringComparer.Ordinal)
    {
        "$schema", "views", "globalSettings"
    };

    private static readonly HashSet<string> KnownViewKeys = new(StringComparer.Ordinal)
    {
        "skip", "forceTemplate", "preferredInit", "asyncPattern",
        "parameterOverrides", "extraSwiftImports", "reason"
    };

    private static readonly HashSet<string> KnownGlobalSettingsKeys = new(StringComparer.Ordinal)
    {
        "maxAsyncChainDepth", "maxClosureParams", "extraSwiftImports"
    };

    private static readonly HashSet<string> KnownAsyncPatternKeys = new(StringComparer.Ordinal)
    {
        "dependencyChain", "resultMonitor"
    };

    /// <summary>
    /// Discovers and loads a bridge hints file.
    /// Discovery order:
    /// 1. CLI --bridge-hints path (if specified and exists; warn if missing)
    /// 2. {moduleName}.bridge-hints.json in outputDirectory
    /// 3. bridge-hints.json in outputDirectory
    /// 4. None found → return null
    /// </summary>
    public static BridgeHintsFile? Load(
        string? cliPath, string outputDirectory, string moduleName, ILogger logger)
    {
        string? resolvedPath = null;
        bool cliSpecified = !string.IsNullOrWhiteSpace(cliPath);

        if (cliSpecified)
        {
            if (File.Exists(cliPath))
            {
                resolvedPath = cliPath;

                // Warn if a discovered file also exists (it will be ignored)
                var moduleSpecific = Path.Combine(outputDirectory, $"{moduleName}.bridge-hints.json");
                var generic = Path.Combine(outputDirectory, "bridge-hints.json");
                if (File.Exists(moduleSpecific))
                    logger.LogWarning("Bridge hints: CLI path specified, ignoring discovered file {Path}", moduleSpecific);
                else if (File.Exists(generic))
                    logger.LogWarning("Bridge hints: CLI path specified, ignoring discovered file {Path}", generic);
            }
            else
            {
                logger.LogWarning("Bridge hints: CLI path {Path} not found, proceeding without hints", cliPath);
                return null;
            }
        }
        else
        {
            // File discovery
            var moduleSpecific = Path.Combine(outputDirectory, $"{moduleName}.bridge-hints.json");
            var generic = Path.Combine(outputDirectory, "bridge-hints.json");

            if (File.Exists(moduleSpecific))
                resolvedPath = moduleSpecific;
            else if (File.Exists(generic))
                resolvedPath = generic;
        }

        if (resolvedPath == null)
            return null;

        return ParseAndValidate(resolvedPath, logger);
    }

    private static BridgeHintsFile? ParseAndValidate(string path, ILogger logger)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bridge hints: failed to read {Path}", path);
            return null;
        }

        // First pass: validate unknown keys with JsonDocument
        try
        {
            using var doc = JsonDocument.Parse(json);
            ValidateUnknownKeys(doc.RootElement, path, logger);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed bridge hints at {Path}: {Message}", path, ex.Message);
            return null;
        }

        // Second pass: deserialize to model
        BridgeHintsFile? hints;
        try
        {
            hints = JsonSerializer.Deserialize(json, BridgeHintsJsonContext.Default.BridgeHintsFile);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed bridge hints at {Path}: {Message}", path, ex.Message);
            return null;
        }

        if (hints == null)
            return null;

        // Log deferred feature usage (once per file, not per view)
        LogDeferredFeatures(hints, logger);

        return hints;
    }

    private static void ValidateUnknownKeys(JsonElement root, string path, ILogger logger)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in root.EnumerateObject())
        {
            if (!KnownRootKeys.Contains(prop.Name))
            {
                logger.LogWarning("Bridge hints: unknown key '{Key}' at {Path} (ignored)", prop.Name, path);
            }
        }

        // Validate view-level keys and nested objects
        if (root.TryGetProperty("views", out var viewsElement) && viewsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var viewProp in viewsElement.EnumerateObject())
            {
                if (viewProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var viewKey in viewProp.Value.EnumerateObject())
                    {
                        if (!KnownViewKeys.Contains(viewKey.Name))
                        {
                            logger.LogWarning("Bridge hints: unknown key '{Key}' in view '{View}' at {Path} (ignored)",
                                viewKey.Name, viewProp.Name, path);
                        }
                    }

                    // Validate asyncPattern keys
                    if (viewProp.Value.TryGetProperty("asyncPattern", out var asyncElement)
                        && asyncElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var asyncKey in asyncElement.EnumerateObject())
                        {
                            if (!KnownAsyncPatternKeys.Contains(asyncKey.Name))
                            {
                                logger.LogWarning("Bridge hints: unknown key '{Key}' in asyncPattern for view '{View}' at {Path} (ignored)",
                                    asyncKey.Name, viewProp.Name, path);
                            }
                        }
                    }
                }
            }
        }

        // Validate globalSettings keys
        if (root.TryGetProperty("globalSettings", out var globalElement) && globalElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var globalKey in globalElement.EnumerateObject())
            {
                if (!KnownGlobalSettingsKeys.Contains(globalKey.Name))
                {
                    logger.LogWarning("Bridge hints: unknown key '{Key}' in globalSettings at {Path} (ignored)",
                        globalKey.Name, path);
                }
            }
        }
    }

    private static void LogDeferredFeatures(BridgeHintsFile hints, ILogger logger)
    {
        if (hints.Views != null)
        {
            bool hasParameterOverrides = false;
            bool hasResultMonitor = false;

            foreach (var (_, viewHint) in hints.Views)
            {
                if (viewHint.ParameterOverrides != null && viewHint.ParameterOverrides.Count > 0)
                    hasParameterOverrides = true;
                if (viewHint.AsyncPattern?.ResultMonitor != null)
                    hasResultMonitor = true;
            }

            if (hasParameterOverrides)
                logger.LogInformation("Bridge hints: parameterOverrides accepted but not yet applied (Phase 4)");
            if (hasResultMonitor)
                logger.LogInformation("Bridge hints: resultMonitor accepted but not yet supported (Phase 4)");
        }

        if (hints.GlobalSettings != null)
        {
            if (hints.GlobalSettings.MaxAsyncChainDepth.HasValue)
                logger.LogInformation("Bridge hints: maxAsyncChainDepth accepted but not yet applied (using default 3)");
            if (hints.GlobalSettings.MaxClosureParams.HasValue)
                logger.LogInformation("Bridge hints: maxClosureParams accepted but not yet applied (using default 4)");
        }
    }
}
