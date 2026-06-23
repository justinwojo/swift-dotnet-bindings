// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Shared lexical brace scanner for the line-based block-boundary finders that walk generated
/// C#/Swift source. A naive scan that counts every <c>{</c>/<c>}</c> miscounts braces that live
/// inside a string literal, character literal, line comment, or block comment — e.g. a Swift default
/// value <c>prefix: String = "}"</c> closes the enclosing block early and truncates a
/// <c>#if targetEnvironment(simulator)</c> guard, breaking the device compile. This scanner reports
/// ONLY structural braces, carrying block-comment nesting across lines so a multi-line <c>/* ... */</c>
/// is handled. On input with no braces inside strings/comments it is byte-for-byte equivalent to a raw
/// <c>{</c>/<c>}</c> count, so callers' existing behavior on clean source is preserved exactly.
/// <para>Lexical scope, by design: it models ordinary double/single-quoted literals (with <c>\</c>
/// escapes), <c>//</c> line comments, and <c>/* */</c> block comments — the only string/comment forms
/// the generator ever writes into the C#/Swift text these finders walk. It does NOT model C# verbatim
/// (<c>@"..."</c>) or raw (<c>"""..."""</c>) strings, or Swift raw/multiline (<c>#"..."#</c> /
/// <c>"""</c>) strings; an unbalanced brace inside one of those would miscount. That is sufficient
/// today because the generator emits none of those forms into the walked text — interpolated
/// <c>$"...{x}..."</c> braces are already inside a plain <c>"..."</c> and are skipped correctly, and
/// the only raw-string literals in the emitter (e.g. <c>ConsumerTargetsEmitter</c>) produce MSBuild
/// <c>.targets</c> XML, which no brace finder scans. If a future emitter writes a verbatim/raw string
/// containing a brace into walked text, extend this scanner (and its tests) rather than re-introducing
/// a raw count at a call site.</para>
/// </summary>
internal static class StructuralBraceScanner
{
    /// <summary>
    /// Walks one line, invoking <paramref name="onStructuralBrace"/> with <c>+1</c> for each structural
    /// <c>{</c> and <c>-1</c> for each structural <c>}</c>, in source order. Skips line comments
    /// (<c>//</c>), block comments (<c>/* */</c>, nesting-aware), and string/character literals (honoring
    /// <c>\</c> escapes). <paramref name="blockCommentDepth"/> carries block-comment nesting across lines
    /// (Swift permits nesting; for C# it never exceeds 1) and must be threaded line to line by the caller.
    /// </summary>
    internal static void ScanLine(string line, ref int blockCommentDepth, Action<int> onStructuralBrace)
    {
        int i = 0;
        int n = line.Length;
        while (i < n)
        {
            char c = line[i];

            if (blockCommentDepth > 0)
            {
                if (c == '*' && i + 1 < n && line[i + 1] == '/') { blockCommentDepth--; i += 2; continue; }
                if (c == '/' && i + 1 < n && line[i + 1] == '*') { blockCommentDepth++; i += 2; continue; }
                i++;
                continue;
            }

            if (c == '/' && i + 1 < n && line[i + 1] == '/')
                break; // line comment — the remainder of the line is non-structural
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                blockCommentDepth++;
                i += 2;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < n)
                {
                    if (line[i] == '\\') { i += 2; continue; } // escape — skip the escaped char
                    if (line[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '{') onStructuralBrace(1);
            else if (c == '}') onStructuralBrace(-1);
            i++;
        }
    }

    /// <summary>
    /// Convenience for callers that only need the line's NET structural brace delta (opens minus closes)
    /// and whether the line contained at least one structural <c>{</c> — the contract the FindBlockEnd
    /// helpers rely on. Threads <paramref name="blockCommentDepth"/> across lines and ORs
    /// <paramref name="sawOpen"/> when any structural <c>{</c> is seen on this line.
    /// </summary>
    internal static int NetLineDelta(string line, ref int blockCommentDepth, ref bool sawOpen)
    {
        int delta = 0;
        bool localSaw = false;
        ScanLine(line, ref blockCommentDepth, d =>
        {
            delta += d;
            if (d > 0) localSaw = true;
        });
        if (localSaw) sawOpen = true;
        return delta;
    }
}
