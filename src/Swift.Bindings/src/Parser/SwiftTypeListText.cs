// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared text helpers for splitting Swift type-list syntax (where-clause constraint lists,
/// protocol-conformance lists) without tearing apart constructed-generic arguments.
/// </summary>
internal static class SwiftTypeListText
{
    /// <summary>
    /// Splits <paramref name="text"/> at commas that sit at angle-bracket depth zero, so the
    /// inner commas of a constructed-generic target (<c>KeyPath&lt;Intent, Parameter&gt;</c>)
    /// stay attached to their clause instead of being split into fragments.
    /// </summary>
    public static List<string> SplitTopLevelCommas(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
