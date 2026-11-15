// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BindingsGeneration;

/// <summary>
/// The transition manifest: the call-plan records a compiled assembly's Swift transitions were
/// planned, judged and routed by, carried inside the assembly so it can be checked without the
/// build tree that produced it. One manifest describes one module (a generated binding's Swift
/// module, a hand-written transition table, or the Apple supplement's generated types); an assembly
/// carries one per module compiled into it.
/// </summary>
/// <remarks>
/// <para>
/// A manifest is an embedded resource. An <c>AssemblyMetadataAttribute</c> keyed
/// <see cref="AttributeKey"/> locates it and states the SHA-256 of its exact bytes
/// (<see cref="AttributeValue"/>). When the module routes a transition through a native bridge, the
/// manifest names the native library and a data symbol (<see cref="NativeSymbol"/>), and every slice
/// of that library exports the symbol holding the same hash as a NUL-terminated string
/// (<see cref="NativeAssembly"/>), so a packaged assembly and native image can be matched.
/// </para>
/// <para>
/// The plan records are the generator's call-plan sidecar and transition-guard sidecar, carried
/// verbatim, and the contract is the lowering-evidence contract their evidence was judged under. A new
/// format would raise <see cref="Schema"/>. This file is self-contained (base class library only):
/// the generator writes manifests with it, and the build's gates and a consumer-side MSBuild task
/// read them with it.
/// </para>
/// </remarks>
public static class SwiftTransitionManifestFormat
{
    /// <summary>The manifest schema this file writes and reads.</summary>
    public const int Schema = 1;

    /// <summary>The <c>kind</c> every manifest names.</summary>
    public const string Kind = "swift-transition-manifest";

    /// <summary>The <c>AssemblyMetadataAttribute</c> key of a manifest locator.</summary>
    public const string AttributeKey = "SwiftBindings.TransitionManifest";

    /// <summary>A manifest's file and resource name suffix.</summary>
    public const string FileSuffix = ".swift-transition-manifest.json";

    /// <summary>The suffix of the C# file holding a manifest's locator attribute.</summary>
    public const string AttributeFileSuffix = ".SwiftTransitionManifest.g.cs";

    /// <summary>The prefix of the native data symbol holding a manifest's hash.</summary>
    public const string NativeSymbolPrefix = "SBTM_";

    /// <summary>A hash's spelling: <c>sha256:</c> and 64 lowercase hex digits.</summary>
    public const string HashPrefix = "sha256:";

    /// <summary>The origin of a generated module's manifest.</summary>
    public const string Generated = "generated";

    /// <summary>The origin of a hand-written transition table's manifest.</summary>
    public const string HandWritten = "hand-written";

    /// <summary>The manifest file (and resource) of <paramref name="stem"/>: a namespace, or an assembly's table.</summary>
    public static string FileName(string stem) => stem + FileSuffix;

    /// <summary>The attribute file beside the manifest of <paramref name="stem"/>.</summary>
    public static string AttributeFileName(string stem) => stem + AttributeFileSuffix;

    /// <summary>The hash of a manifest's exact bytes.</summary>
    public static string Hash(ReadOnlySpan<byte> manifest) =>
        HashPrefix + Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();

    /// <summary>The hash of a manifest's text, as written (UTF-8, no byte-order mark).</summary>
    public static string Hash(string manifest) => Hash(Encoding.UTF8.GetBytes(manifest));

    /// <summary>Whether <paramref name="hash"/> is spelled as <see cref="Hash(ReadOnlySpan{byte})"/> spells one.</summary>
    public static bool IsHash(string? hash) =>
        hash is { Length: 71 } && hash.StartsWith(HashPrefix, StringComparison.Ordinal)
        && hash.AsSpan(HashPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>The native data symbol (without the Mach-O underscore) holding <paramref name="module"/>'s manifest hash.</summary>
    public static string NativeSymbol(string module)
    {
        var name = new StringBuilder(NativeSymbolPrefix);
        foreach (var c in module)
            name.Append(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');
        return name.ToString();
    }

    /// <summary>The locator attribute's value: the schema, the resource name and the manifest hash.</summary>
    public static string AttributeValue(string resource, string hash) =>
        $"schema={Schema};resource={resource};{hash.Replace(HashPrefix, "sha256=", StringComparison.Ordinal)}";

    /// <summary>
    /// Reads a locator attribute's value. Returns false, with why, when it is not exactly
    /// <c>schema=N;resource=NAME;sha256=HEX</c>. A schema this reader does not know still parses,
    /// so the caller can say which schema it met.
    /// </summary>
    public static bool TryParseAttributeValue(string? value, out int schema, out string resource, out string hash, out string? error)
    {
        schema = 0;
        resource = hash = string.Empty;
        error = null;
        var parts = (value ?? string.Empty).Split(';');
        if (parts.Length != 3 || !parts[0].StartsWith("schema=", StringComparison.Ordinal)
            || !parts[1].StartsWith("resource=", StringComparison.Ordinal) || !parts[2].StartsWith("sha256=", StringComparison.Ordinal))
        {
            error = $"the locator '{value}' is not schema=N;resource=NAME;sha256=HEX";
            return false;
        }
        if (!int.TryParse(parts[0]["schema=".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out schema))
        {
            error = $"the locator '{value}' names no schema number";
            return false;
        }
        resource = parts[1]["resource=".Length..];
        hash = HashPrefix + parts[2]["sha256=".Length..];
        if (resource.Length == 0 || !IsHash(hash))
        {
            error = $"the locator '{value}' names no resource or no SHA-256";
            return false;
        }
        return true;
    }

    /// <summary>The C# that declares the locators of <paramref name="manifests"/> (resource name, hash).</summary>
    public static string AttributeSource(IEnumerable<(string Resource, string Hash)> manifests)
    {
        var text = new StringBuilder();
        text.Append("// <auto-generated>\n");
        text.Append("// Locates and authenticates this assembly's Swift transition manifest(s): the embedded resource and\n");
        text.Append("// the SHA-256 of its bytes. Written by Swift.Bindings with the manifest; do not edit.\n");
        text.Append("// </auto-generated>\n");
        foreach (var (resource, hash) in manifests)
            text.Append("[assembly: global::System.Reflection.AssemblyMetadataAttribute(\"")
                .Append(AttributeKey).Append("\", \"").Append(AttributeValue(resource, hash)).Append("\")]\n");
        return text.ToString();
    }

    /// <summary>
    /// The assembly that exports <paramref name="symbol"/> holding <paramref name="hash"/>, appended to a
    /// bridge assembly so every slice the bridges link into carries it. The symbol is a constant, kept
    /// by the linker even if nothing references it.
    /// </summary>
    public static string NativeAssembly(string symbol, string hash) =>
        $"/* The SHA-256 of the transition manifest these bridges were routed by. */\n" +
        $".section __TEXT,__const\n" +
        $".globl _{symbol}\n" +
        $".no_dead_strip _{symbol}\n" +
        $"_{symbol}:\n" +
        $"  .asciz \"{hash}\"\n";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The manifest of one module, as text. <paramref name="plans"/> is the module's call-plan sidecar
    /// and <paramref name="guards"/> its transition-guard sidecar, if it has one; both are carried as
    /// their JSON values, re-serialized indented with LF line endings. The manifest names the native library and symbol exactly when the plans route a
    /// transition through a bridge. Deterministic: the same inputs give the same bytes.
    /// </summary>
    /// <param name="generator">The generator that wrote it, without build metadata; null for a hand-written table, whose committed products must not change with each generator build.</param>
    /// <param name="contract">The plan, facts and collector versions the plans were judged under.</param>
    /// <param name="nativeLibrary">The native library that defines the module's bridges.</param>
    public static string Compose(
        string module, string origin, string? generator, (int Plan, int Facts, int Collector) contract,
        string? nativeLibrary, string plans, string? guards)
    {
        var plansNode = JsonNode.Parse(plans) as JsonObject ?? throw new InvalidDataException("the call plans are not a JSON object");
        var guardsNode = guards is null ? null : JsonNode.Parse(guards) as JsonObject ?? throw new InvalidDataException("the transition guards are not a JSON object");
        var bridged = BridgedSymbols(plansNode);
        if (bridged.Length > 0 && string.IsNullOrWhiteSpace(nativeLibrary))
            throw new InvalidDataException($"{module} routes {bridged.Length} transition(s) through bridges but names no native library that defines them");
        var root = new JsonObject
        {
            ["kind"] = Kind,
            ["schema"] = Schema,
            ["module"] = module,
            ["origin"] = origin,
        };
        if (generator is not null)
            root["generator"] = generator;
        root["contract"] = new JsonObject
        {
            ["planVersion"] = contract.Plan,
            ["factsVersion"] = contract.Facts,
            ["collectorVersion"] = contract.Collector,
        };
        root["native"] = bridged.Length == 0
            ? null
            : new JsonObject
            {
                ["library"] = nativeLibrary,
                ["symbol"] = NativeSymbol(module),
                ["bridges"] = new JsonArray(bridged.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            };
        root["plans"] = plansNode;
        root["guards"] = guardsNode;
        return root.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>The bridges a call-plan document routes through (its bridged coverage entries' symbols), in ordinal order.</summary>
    public static ImmutableArray<string> BridgedSymbols(JsonObject plans) =>
        (plans["coverage"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(e => e["bridged"] is JsonValue b && b.TryGetValue<bool>(out var isBridged) && isBridged)
            .Select(e => e["symbol"]?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    /// <summary>
    /// Reads a manifest's bytes. Throws <see cref="InvalidDataException"/> when they are not a
    /// manifest of this schema, or when the manifest contradicts itself: its plans of another module,
    /// a native section that does not name the bridges the plans route, or a native symbol other than
    /// the module's.
    /// </summary>
    public static SwiftTransitionManifest Parse(byte[] bytes, string origin)
    {
        try
        {
            return ParseCore(bytes, origin);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // A typed read of a value of another JSON kind (a number where a string belongs).
            throw new InvalidDataException($"{origin} does not read as a manifest: {ex.Message}", ex);
        }
    }

    private static SwiftTransitionManifest ParseCore(byte[] bytes, string origin)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException($"{origin} is not a JSON object");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{origin} is not JSON: {ex.Message}", ex);
        }
        string Text(JsonObject node, string name) =>
            node[name] is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0
                ? s
                : throw new InvalidDataException($"{origin} names no {name}");
        int Number(JsonObject node, string name) =>
            node[name] is JsonValue value && value.TryGetValue<int>(out var n)
                ? n
                : throw new InvalidDataException($"{origin} names no {name}");

        if (Text(root, "kind") != Kind)
            throw new InvalidDataException($"{origin} is not a {Kind}");
        var schema = Number(root, "schema");
        if (schema != Schema)
            throw new InvalidDataException($"{origin} has manifest schema {schema}; this reader reads {Schema}");
        var module = Text(root, "module");
        var manifestOrigin = Text(root, "origin");
        if (manifestOrigin is not (Generated or HandWritten))
            throw new InvalidDataException($"{origin} has origin '{manifestOrigin}'");
        var contract = root["contract"] as JsonObject ?? throw new InvalidDataException($"{origin} names no contract");
        var plans = root["plans"] as JsonObject ?? throw new InvalidDataException($"{origin} carries no call plans");
        if (Text(plans, "module") != module)
            throw new InvalidDataException($"{origin} is the manifest of {module} but carries the call plans of {Text(plans, "module")}");
        if (Number(plans, "planVersion") != Number(contract, "planVersion") || Number(plans, "factsVersion") != Number(contract, "factsVersion"))
            throw new InvalidDataException($"{origin}'s contract does not match the plan and facts versions of the plans it carries");
        var guards = root["guards"] switch
        {
            null => null,
            JsonObject g => g,
            _ => throw new InvalidDataException($"{origin}'s transition guards are not an object"),
        };
        var bridged = BridgedSymbols(plans);
        string? library = null, symbol = null;
        switch (root["native"])
        {
            case null when bridged.Length > 0:
                throw new InvalidDataException($"{origin} routes {bridged.Length} transition(s) through bridges but names no native image");
            case null:
                break;
            case JsonObject native:
                library = Text(native, "library");
                symbol = Text(native, "symbol");
                if (symbol != NativeSymbol(module))
                    throw new InvalidDataException($"{origin} names the native symbol {symbol}; {module}'s is {NativeSymbol(module)}");
                var listed = (native["bridges"] as JsonArray ?? throw new InvalidDataException($"{origin}'s native section lists no bridges"))
                    .Select(b => b?.GetValue<string>() ?? "").ToImmutableArray();
                if (!listed.SequenceEqual(bridged, StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"{origin}'s native section lists the bridges [{string.Join(", ", listed)}], but its plans route [{string.Join(", ", bridged)}]");
                if (bridged.Length == 0)
                    throw new InvalidDataException($"{origin} names a native image but routes nothing through a bridge");
                break;
            default:
                throw new InvalidDataException($"{origin}'s native section is not an object");
        }
        return new SwiftTransitionManifest(
            module, manifestOrigin, root["generator"]?.GetValue<string>(),
            (Number(contract, "planVersion"), Number(contract, "factsVersion"), Number(contract, "collectorVersion")),
            library, symbol, bridged,
            plans.ToJsonString(Indented) + "\n",
            guards is null ? null : guards.ToJsonString(Indented) + "\n");
    }
}

/// <summary>A read transition manifest (<see cref="SwiftTransitionManifestFormat.Parse"/>).</summary>
/// <param name="Module">The module whose plans it carries.</param>
/// <param name="Origin"><see cref="SwiftTransitionManifestFormat.Generated"/> or <see cref="SwiftTransitionManifestFormat.HandWritten"/>.</param>
/// <param name="Generator">The generator that wrote it; null for a hand-written table.</param>
/// <param name="Contract">The plan, facts and collector versions its plans were judged under.</param>
/// <param name="NativeLibrary">The native library that defines its bridges; null when it routes none.</param>
/// <param name="NativeSymbol">The data symbol (without the Mach-O underscore) that library exports holding the manifest hash; null when it routes none.</param>
/// <param name="Bridges">The bridges its plans route through, in ordinal order.</param>
/// <param name="Plans">The call-plan document, as JSON.</param>
/// <param name="Guards">The transition-guard document, as JSON; null when the module records none.</param>
public sealed record SwiftTransitionManifest(
    string Module,
    string Origin,
    string? Generator,
    (int Plan, int Facts, int Collector) Contract,
    string? NativeLibrary,
    string? NativeSymbol,
    ImmutableArray<string> Bridges,
    string Plans,
    string? Guards);
