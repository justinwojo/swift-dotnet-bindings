// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BindingsGeneration.ObjC;

/// <summary>Reads Clang's simple record-layout dump; it does not calculate C layout.</summary>
internal static class ClangRecordLayoutParser
{
    internal static Dictionary<string, ObjCRecordLayout> Parse(string? text)
    {
        var result = new Dictionary<string, ObjCRecordLayout>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;
        foreach (Match match in Regex.Matches(text,
            @"Type: (?<name>[^\r\n]+)\s+Layout: <ASTRecordLayout\s+Size:(?<size>\d+)\s+DataSize:\d+\s+Alignment:(?<align>\d+)\s+FieldOffsets: \[(?<fields>[\d,\s]*)\]>", RegexOptions.CultureInvariant))
        {
            var fields = match.Groups["fields"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            result[match.Groups["name"].Value.Trim()] = new ObjCRecordLayout(
                long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture),
                long.Parse(match.Groups["align"].Value, CultureInfo.InvariantCulture), fields);
        }
        return result;
    }

    internal static ObjCRecordLayout? Find(JsonElement node, string? file, IReadOnlyDictionary<string, ObjCRecordLayout> layouts)
    {
        var tag = node.TryGetProperty("tagUsed", out var t) ? t.GetString() : "struct";
        if (node.TryGetProperty("name", out var n) && !string.IsNullOrEmpty(n.GetString()))
            return layouts.GetValueOrDefault($"{tag} {n.GetString()}");
        if (!node.TryGetProperty("loc", out var loc)) return null;
        if (loc.TryGetProperty("expansionLoc", out var expansion)) loc = expansion;
        if (loc.TryGetProperty("file", out var f)) file = f.GetString();
        if (file == null) return null;
        int line = loc.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
        int column = loc.TryGetProperty("col", out var c) ? c.GetInt32() : 0;
        // Clang omits repeated line numbers. Source offsets are UTF-8 byte offsets.
        if (line == 0 && loc.TryGetProperty("offset", out var offset) && File.Exists(file))
        {
            var bytes = File.ReadAllBytes(file);
            var end = Math.Min(offset.GetInt32(), bytes.Length);
            line = 1;
            for (var i = 0; i < end; i++) if (bytes[i] == '\n') line++;
        }
        return layouts.GetValueOrDefault($"{tag} (unnamed at {file}:{line}:{column})")
            ?? layouts.GetValueOrDefault($"{tag} (anonymous at {file}:{line}:{column})");
    }
}
