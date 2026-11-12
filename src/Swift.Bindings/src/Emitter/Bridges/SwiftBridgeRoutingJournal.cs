// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration;

/// <summary>
/// What the wrapper compile's bridge routing (<see cref="SwiftBridgeFinalizer"/>) rewrote in a module's
/// emitted C#, beside that C# as <c>{namespace}.swift-bridge-routing.json</c>. Each rewrite names the
/// file, the import as generation emitted it and the shim and twin written in its place, so the next
/// wrapper compile can restore the C# generation wrote and route it again from its own receipt.
/// </summary>
/// <remarks>
/// The journal is written before the C# it describes, so a build interrupted between the two leaves a
/// file the journal says is rewritten that is not yet: <see cref="Revert"/> accepts an import still in
/// its original form. Generation removes the journal, as the C# it then writes is the original. With no
/// journal, the module's C# is generation's own: the routing never touches C# whose plans generation
/// judged itself.
/// </remarks>
public sealed record SwiftBridgeRoutingJournal
{
    /// <summary>The journal schema. Bump it on any change to the journal's shape.</summary>
    public const int SchemaVersion = 1;

    public required int Schema { get; init; }

    /// <summary>The module whose C# was rewritten.</summary>
    public required string Module { get; init; }

    /// <summary>The library the twins import the bridges from: the module's wrapper.</summary>
    public required string Library { get; init; }

    /// <summary>Every rewrite in the module's C#, ordered by file, member and entry point.</summary>
    public required ImmutableArray<SwiftBridgeRewrite> Rewrites { get; init; }

    /// <summary>The journal's file name for a module namespace.</summary>
    public static string FileName(string @namespace) => $"{@namespace}.swift-bridge-routing.json";

    /// <summary>A directory glob that matches every module's journal.</summary>
    public const string FilePattern = "*.swift-bridge-routing.json";

    public static SwiftBridgeRoutingJournal Read(string path)
    {
        var journal = JsonSerializer.Deserialize(File.ReadAllText(path), SwiftBridgeRoutingJournalJsonContext.Default.SwiftBridgeRoutingJournal)
            ?? throw new InvalidDataException($"{path} holds no bridge routing journal");
        if (journal.Schema != SchemaVersion)
            throw new InvalidDataException($"{path} has journal schema {journal.Schema}; this generator reads {SchemaVersion}");
        return journal;
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, SwiftBridgeRoutingJournalJsonContext.Default.SwiftBridgeRoutingJournal) + "\n";

    /// <summary>Writes the journal atomically: a reader sees the previous journal or this whole one.</summary>
    public void Write(string path) => WriteAtomically(path, ToJson());

    /// <summary>
    /// <paramref name="source"/>, the file <paramref name="file"/>, with every rewrite the journal records
    /// in it undone. A rewrite whose replacement is not in the file but whose original is was never
    /// written (the build stopped between journal and C#) and is accepted as undone.
    /// </summary>
    /// <exception cref="InvalidDataException">The file holds neither form of a recorded rewrite, or not as often as recorded.</exception>
    public string Revert(string file, string source)
    {
        var reverted = source;
        foreach (var group in Rewrites.Where(r => r.File == file).GroupBy(r => (r.Original, r.Replacement)))
        {
            var expected = group.Count();
            var (original, replacement) = group.Key;
            var rewritten = Occurrences(reverted, replacement);
            if (rewritten == expected)
                reverted = reverted.Replace(replacement, original, StringComparison.Ordinal);
            else if (rewritten != 0 || Occurrences(reverted, original) < expected)
                throw new InvalidDataException(
                    $"{file} does not hold the bridge rewrite of {group.First().Member} ({group.First().EntryPoint}) the routing journal records " +
                    $"({rewritten} of {expected} rewritten); the generated C# was changed by something other than its generator");
        }
        return reverted;
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> through a sibling temporary file and a rename.</summary>
    internal static void WriteAtomically(string path, string text)
    {
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

/// <summary>Source-generated, trim-safe serialization of the routing journal.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SwiftBridgeRoutingJournal))]
internal partial class SwiftBridgeRoutingJournalJsonContext : JsonSerializerContext
{
}
