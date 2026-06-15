// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared text helpers for splitting Swift type-list syntax without tearing apart constructed
/// generics, closures, or string-literal defaults.
///
/// Two distinct splitters live here on purpose:
/// <list type="bullet">
///   <item><see cref="SplitTopLevelCommas"/> — the narrow, <em>angle-bracket-only</em> splitter
///   for where-clause constraint lists and protocol-conformance lists. Its existing consumers
///   (generic-signature/where-clause parsing) are a different grammar from a value-parameter
///   list and are intentionally NOT migrated to the parameter splitter below.</item>
///   <item><see cref="SplitTopLevelParameters"/> — the robust value-parameter-list splitter that
///   also tracks <c>()</c>/<c>[]</c>, string literals, and the closure return arrow (so the
///   <c>&gt;</c> in <c>-&gt;</c> is not mistaken for a closing angle bracket). This is the single
///   implementation the per-emitter/per-parser <c>SplitParameters</c> clones now delegate to.</item>
/// </list>
/// </summary>
internal static class SwiftTypeListText
{
    /// <summary>
    /// Splits <paramref name="text"/> at commas that sit at angle-bracket depth zero, so the
    /// inner commas of a constructed-generic target (<c>KeyPath&lt;Intent, Parameter&gt;</c>)
    /// stay attached to their clause instead of being split into fragments.
    ///
    /// This tracks only angle brackets by design — it serves where-clause constraint and
    /// conformance lists, where parentheses/brackets/closures do not appear at the top level.
    /// For value-parameter lists (which can contain closures and bracketed types) use
    /// <see cref="SplitTopLevelParameters"/>.
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

    /// <summary>
    /// Splits a Swift value-parameter list (the text between a declaration's outermost
    /// parentheses) at commas that sit at nesting depth zero. Tracks angle brackets, parentheses,
    /// square brackets, and double-quoted string literals, and — critically — does NOT treat the
    /// <c>&gt;</c> of a closure return arrow (<c>-&gt;</c>) as a closing angle bracket. Without
    /// that arrow guard a parameter list like <c>value: T, transform: (T) -&gt; U, flag: Bool</c>
    /// drives the depth counter negative at the arrow and merges the trailing parameters.
    ///
    /// This is the single shared implementation that the previously-duplicated per-class
    /// <c>SplitParameters</c> helpers delegate to (Finding 49 grammar consolidation).
    /// </summary>
    public static List<string> SplitTopLevelParameters(string paramStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        bool inString = false;

        for (int i = 0; i < paramStr.Length; i++)
        {
            char c = paramStr[i];
            // Track string literals — skip commas inside "..."
            if (c == '"' && (i == 0 || paramStr[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (c == '<' || c == '(' || c == '[') depth++;
            // Don't treat '>' in '->' (closure return arrow) as a closing bracket
            if (c == '>' && !(i > 0 && paramStr[i - 1] == '-')) depth--;
            else if (c == ')' || c == ']') depth--;
            if (c == ',' && depth == 0)
            {
                result.Add(paramStr.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(paramStr.Substring(start));
        return result;
    }

    /// <summary>
    /// Returns the index of the first closure/function return arrow (<c>-&gt;</c>) that sits at
    /// nesting depth zero, or <c>-1</c> if there is none. Tracks angle brackets, parentheses,
    /// square brackets, and string literals so an arrow nested inside a generic argument or a
    /// closure-typed parameter (e.g. the inner <c>-&gt;</c> of <c>Dictionary&lt;String, () -&gt; Int&gt;</c>)
    /// is skipped rather than mistaken for the top-level return arrow.
    ///
    /// Replaces the unguarded <c>IndexOf("-&gt;")</c> scans that break on constructed generics.
    /// </summary>
    public static int IndexOfTopLevelArrow(string text)
    {
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                if (depth == 0)
                    return i;
                i++; // consume the '>' so the arrow does not perturb depth tracking
                continue;
            }
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
        }
        return -1;
    }
}
