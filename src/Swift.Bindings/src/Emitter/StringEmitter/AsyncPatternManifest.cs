// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Root model for an async-pattern manifest sidecar file. The manifest supplies additional
/// <see cref="AsyncViewPattern"/> descriptors from outside the generator, keyed by
/// <c>{moduleName}.{viewName}</c>, so a caller can bridge an async View the generator's own
/// registry does not know about. With no manifest supplied, nothing about generation changes.
/// </summary>
public class AsyncPatternManifestFile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("patterns")]
    public List<AsyncPatternEntry>? Patterns { get; set; }
}

/// <summary>
/// One View's async bridge descriptor, mirroring <see cref="AsyncViewPattern"/> in a
/// JSON-friendly shape. Enum-valued fields are carried as strings and parsed on load so a
/// typo produces a named warning rather than a silently wrong kind.
/// </summary>
public class AsyncPatternEntry
{
    [JsonPropertyName("moduleName")]
    public string? ModuleName { get; set; }

    [JsonPropertyName("viewName")]
    public string? ViewName { get; set; }

    [JsonPropertyName("sessionClassName")]
    public string? SessionClassName { get; set; }

    [JsonPropertyName("extraSwiftImports")]
    public List<string>? ExtraSwiftImports { get; set; }

    [JsonPropertyName("sessionFields")]
    public List<AsyncSessionFieldEntry>? SessionFields { get; set; }

    [JsonPropertyName("flattenedParams")]
    public List<AsyncFlatParamEntry>? FlattenedParams { get; set; }

    [JsonPropertyName("constructionChain")]
    public List<AsyncConstructionStepEntry>? ConstructionChain { get; set; }

    [JsonPropertyName("resultCallback")]
    public AsyncResultCallbackEntry? ResultCallback { get; set; }

    [JsonPropertyName("viewInitArgs")]
    public List<ConstructionArgEntry>? ViewInitArgs { get; set; }
}

/// <summary>A session field retained by the generated Swift session class.</summary>
public class AsyncSessionFieldEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("swiftType")]
    public string? SwiftType { get; set; }
}

/// <summary>A flattened leaf parameter on the generated <c>CreateAsync</c> factory.</summary>
public class AsyncFlatParamEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("swiftAbiType")]
    public string? SwiftAbiType { get; set; }

    [JsonPropertyName("csharpPInvokeType")]
    public string? CSharpPInvokeType { get; set; }

    [JsonPropertyName("swiftConversion")]
    public string? SwiftConversion { get; set; }

    [JsonPropertyName("csharpConversion")]
    public string? CSharpConversion { get; set; }

    [JsonPropertyName("bridgeTypeName")]
    public string? BridgeTypeName { get; set; }

    [JsonPropertyName("csharpTypeName")]
    public string? CSharpTypeName { get; set; }

    [JsonPropertyName("sourceModule")]
    public string? SourceModule { get; set; }

    [JsonPropertyName("isObjCBridgeable")]
    public bool IsObjCBridgeable { get; set; }

    [JsonPropertyName("isSimpleEnum")]
    public bool IsSimpleEnum { get; set; }

    /// <summary>
    /// The C# default for this parameter, or null for none. Only honoured for <c>Bool</c>, and
    /// deliberately typed rather than a literal string — a manifest must not be able to inject
    /// arbitrary text into a generated signature.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    public bool? DefaultValue { get; set; }
}

/// <summary>One step of the async construction chain.</summary>
public class AsyncConstructionStepEntry
{
    [JsonPropertyName("variableName")]
    public string? VariableName { get; set; }

    [JsonPropertyName("swiftTypeName")]
    public string? SwiftTypeName { get; set; }

    [JsonPropertyName("isAsync")]
    public bool IsAsync { get; set; }

    [JsonPropertyName("throws")]
    public bool Throws { get; set; }

    [JsonPropertyName("args")]
    public List<ConstructionArgEntry>? Args { get; set; }

    [JsonPropertyName("factoryMethod")]
    public string? FactoryMethod { get; set; }
}

/// <summary>An argument to a construction step or to the View's initializer.</summary>
public class ConstructionArgEntry
{
    [JsonPropertyName("paramLabel")]
    public string? ParamLabel { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>The result-monitor configuration, including an optional payload descriptor.</summary>
public class AsyncResultCallbackEntry
{
    [JsonPropertyName("sourceFieldName")]
    public string? SourceFieldName { get; set; }

    [JsonPropertyName("awaitMethodName")]
    public string? AwaitMethodName { get; set; }

    [JsonPropertyName("resultCases")]
    public List<AsyncResultCaseEntry>? ResultCases { get; set; }

    [JsonPropertyName("payload")]
    public AsyncResultPayloadEntry? Payload { get; set; }
}

/// <summary>One case of the monitored result enum.</summary>
public class AsyncResultCaseEntry
{
    [JsonPropertyName("swiftCase")]
    public string? SwiftCase { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("carriesPayload")]
    public bool CarriesPayload { get; set; }
}

/// <summary>The type a result case carries across the callback ABI.</summary>
public class AsyncResultPayloadEntry
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("swiftTypeName")]
    public string? SwiftTypeName { get; set; }

    [JsonPropertyName("csharpTypeName")]
    public string? CSharpTypeName { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible deserialization.
/// </summary>
[JsonSerializable(typeof(AsyncPatternManifestFile))]
internal partial class AsyncPatternManifestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads async View patterns from a manifest sidecar file. A manifest is only ever consulted
/// when a caller names one explicitly; there is no directory discovery, because these
/// descriptors change what the generator emits rather than nudging its heuristics.
/// </summary>
public static class AsyncPatternManifestLoader
{
    /// <summary>
    /// Loads the manifest at <paramref name="path"/> and returns the patterns it declares,
    /// keyed by <c>{moduleName}.{viewName}</c>. Returns null when no path was supplied.
    /// A malformed file, or an entry the loader cannot make sense of, is reported and dropped:
    /// a manifest is developer input, so a mistake in it should be readable rather than fatal.
    /// </summary>
    public static IReadOnlyDictionary<string, AsyncViewPattern>? Load(string? path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
        {
            logger.LogWarning("Async pattern manifest: {Path} not found, proceeding without it", path);
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Async pattern manifest: failed to read {Path}", path);
            return null;
        }

        AsyncPatternManifestFile? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                json, AsyncPatternManifestJsonContext.Default.AsyncPatternManifestFile);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed async pattern manifest at {Path}: {Message}", path, ex.Message);
            return null;
        }

        if (manifest?.Patterns == null || manifest.Patterns.Count == 0)
        {
            logger.LogWarning("Async pattern manifest at {Path} declares no patterns", path);
            return null;
        }

        var result = new Dictionary<string, AsyncViewPattern>(StringComparer.Ordinal);
        foreach (var entry in manifest.Patterns)
        {
            // JSON permits a bare `null` anywhere an object is expected, so every element the
            // deserializer hands back is nullable no matter what the schema says.
            if (entry is null)
            {
                logger.LogWarning("Async pattern manifest at {Path}: a null pattern entry, skipping it", path);
                continue;
            }

            var converted = Convert(entry, path, logger);
            if (converted == null)
                continue;

            var key = $"{entry.ModuleName}.{entry.ViewName}";
            if (!result.TryAdd(key, converted))
                logger.LogWarning("Async pattern manifest at {Path}: duplicate pattern for {Key}, keeping the first", path, key);
        }

        logger.LogInformation("Async pattern manifest: loaded {Count} pattern(s) from {Path}", result.Count, path);
        return result.Count > 0 ? result : null;
    }

    private static AsyncViewPattern? Convert(AsyncPatternEntry entry, string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(entry.ModuleName) ||
            string.IsNullOrWhiteSpace(entry.ViewName) ||
            string.IsNullOrWhiteSpace(entry.SessionClassName))
        {
            logger.LogWarning(
                "Async pattern manifest at {Path}: a pattern is missing moduleName/viewName/sessionClassName, skipping it", path);
            return null;
        }

        var label = $"{entry.ModuleName}.{entry.ViewName}";

        var flatParams = new List<AsyncFlatParam>();
        foreach (var p in entry.FlattenedParams ?? new List<AsyncFlatParamEntry>())
        {
            if (p is null ||
                string.IsNullOrWhiteSpace(p.Name) ||
                string.IsNullOrWhiteSpace(p.SwiftAbiType) ||
                string.IsNullOrWhiteSpace(p.CSharpPInvokeType))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} has an incomplete flattened param, skipping the pattern", path, label);
                return null;
            }
            if (!TryParseKind<AsyncFlatParamKind>(p.Kind, out var paramKind))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} param '{Name}' has unknown kind '{Kind}', skipping the pattern", path, label, p.Name, p.Kind);
                return null;
            }
            flatParams.Add(new AsyncFlatParam(
                p.Name, paramKind, p.SwiftAbiType, p.CSharpPInvokeType,
                p.SwiftConversion, p.CSharpConversion, p.BridgeTypeName, p.CSharpTypeName,
                p.SourceModule, p.IsObjCBridgeable, p.IsSimpleEnum, p.DefaultValue));
        }

        var chain = new List<AsyncConstructionStep>();
        foreach (var s in entry.ConstructionChain ?? new List<AsyncConstructionStepEntry>())
        {
            if (s is null || string.IsNullOrWhiteSpace(s.VariableName) || string.IsNullOrWhiteSpace(s.SwiftTypeName))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} has an incomplete construction step, skipping the pattern", path, label);
                return null;
            }
            var args = ConvertArgs(s.Args, path, label, logger);
            if (args == null)
                return null;
            chain.Add(new AsyncConstructionStep(
                s.VariableName, s.SwiftTypeName, s.IsAsync, s.Throws, args, s.FactoryMethod));
        }

        AsyncResultCallbackConfig? resultCallback = null;
        if (entry.ResultCallback is { } rc)
        {
            if (string.IsNullOrWhiteSpace(rc.SourceFieldName) || string.IsNullOrWhiteSpace(rc.AwaitMethodName))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} resultCallback is missing sourceFieldName/awaitMethodName, skipping the pattern", path, label);
                return null;
            }

            var cases = new List<AsyncResultCase>();
            foreach (var c in rc.ResultCases ?? new List<AsyncResultCaseEntry>())
            {
                if (c is null || string.IsNullOrWhiteSpace(c.SwiftCase))
                {
                    logger.LogWarning("Async pattern manifest at {Path}: {Key} has a result case with no swiftCase, skipping the pattern", path, label);
                    return null;
                }
                cases.Add(new AsyncResultCase(c.SwiftCase, c.Code, c.CarriesPayload));
            }

            AsyncResultPayload? payload = null;
            if (rc.Payload is { } pl)
            {
                if (string.IsNullOrWhiteSpace(pl.SwiftTypeName) || string.IsNullOrWhiteSpace(pl.CSharpTypeName))
                {
                    logger.LogWarning("Async pattern manifest at {Path}: {Key} payload is missing swiftTypeName/csharpTypeName, skipping the pattern", path, label);
                    return null;
                }
                if (!TryParseKind<AsyncResultPayloadKind>(pl.Kind, out var payloadKind))
                {
                    logger.LogWarning("Async pattern manifest at {Path}: {Key} payload has unknown kind '{Kind}', skipping the pattern", path, label, pl.Kind);
                    return null;
                }
                payload = new AsyncResultPayload(payloadKind, pl.SwiftTypeName, pl.CSharpTypeName);

                // A payload nothing binds is a manifest that will silently emit a null result.
                if (!cases.Any(c => c.CarriesPayload))
                {
                    logger.LogWarning("Async pattern manifest at {Path}: {Key} declares a payload but no result case carries it, skipping the pattern", path, label);
                    return null;
                }
            }
            else if (cases.Any(c => c.CarriesPayload))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} marks a result case as carrying a payload but declares no payload type, skipping the pattern", path, label);
                return null;
            }

            resultCallback = new AsyncResultCallbackConfig(rc.SourceFieldName, rc.AwaitMethodName, cases, payload);
        }

        var sessionFields = new List<AsyncSessionField>();
        foreach (var f in entry.SessionFields ?? new List<AsyncSessionFieldEntry>())
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.SwiftType))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} has an incomplete session field, skipping the pattern", path, label);
                return null;
            }
            sessionFields.Add(new AsyncSessionField(f.Name, f.SwiftType));
        }

        var extraSwiftImports = entry.ExtraSwiftImports ?? new List<string>();
        foreach (var import in extraSwiftImports)
        {
            // A blank element reaches the bridge emitter as a bare `import` line, so the Swift
            // that gets written out no longer compiles. Reject the pattern here, where the warning
            // can still name the manifest and the key, instead of at swiftc.
            if (string.IsNullOrWhiteSpace(import))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} has a blank extra Swift import, skipping the pattern", path, label);
                return null;
            }
        }

        var viewInitArgs = ConvertArgs(entry.ViewInitArgs, path, label, logger);
        if (viewInitArgs == null)
            return null;

        return new AsyncViewPattern(
            ViewName: entry.ViewName,
            SessionClassName: entry.SessionClassName,
            ExtraSwiftImports: extraSwiftImports.ToArray(),
            SessionFields: sessionFields.ToArray(),
            FlattenedParams: flatParams.ToArray(),
            ConstructionChain: chain,
            ResultCallback: resultCallback,
            ViewInitArgs: viewInitArgs.Count > 0 ? viewInitArgs : null);
    }

    private static List<ConstructionArg>? ConvertArgs(
        List<ConstructionArgEntry>? entries, string path, string label, ILogger logger)
    {
        var args = new List<ConstructionArg>();
        foreach (var a in entries ?? new List<ConstructionArgEntry>())
        {
            if (a is null || string.IsNullOrWhiteSpace(a.ParamLabel) || a.Value == null)
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} has an incomplete construction arg, skipping the pattern", path, label);
                return null;
            }
            if (!TryParseKind<ConstructionArgKind>(a.Kind, out var argKind))
            {
                logger.LogWarning("Async pattern manifest at {Path}: {Key} arg '{Label}' has unknown kind '{Kind}', skipping the pattern", path, label, a.ParamLabel, a.Kind);
                return null;
            }
            args.Add(new ConstructionArg(a.ParamLabel, argKind, a.Value));
        }
        return args;
    }

    private static bool TryParseKind<TEnum>(string? raw, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        return !string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw, ignoreCase: true, out value)
               && Enum.IsDefined(value);
    }
}
