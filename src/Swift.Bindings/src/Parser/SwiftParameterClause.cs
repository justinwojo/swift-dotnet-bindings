// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// The one reader of a declaration's parameter clause in interface text. A default value is
/// source text, so a string literal in it (<c>sep: String = ")"</c>, <c>"("</c>, <c>") throws"</c>)
/// may hold any character: every scan that looks for the end of the clause, a top-level comma,
/// colon or return arrow, or the effects after the clause skips string literals through
/// <see cref="SkipStringLiteral"/>. Every hand-written parameter-clause scan in the generator
/// goes through here, so the effects, the parameters and the labels of one declaration are
/// always read from the same clause.
/// </summary>
internal static class SwiftParameterClause
{
    /// <summary>
    /// Whether a string literal opens at <paramref name="i"/>: a <c>"</c>, or a raw string's run
    /// of <c>#</c> followed by <c>"</c> (a bare <c>#file</c> or <c>#selector</c> is not one).
    /// </summary>
    public static bool IsStringLiteralStart(string text, int i)
    {
        if (i >= text.Length)
            return false;
        if (text[i] == '"')
            return true;
        if (text[i] != '#')
            return false;
        var j = i;
        while (j < text.Length && text[j] == '#')
            j++;
        return j < text.Length && text[j] == '"';
    }

    /// <summary>
    /// The index just past the string literal opening at <paramref name="start"/> (see
    /// <see cref="IsStringLiteralStart"/>): an ordinary, multi-line (<c>"""</c>) or raw
    /// (<c>#"…"#</c>) literal, backslash escapes and interpolations (<c>\(…)</c>, <c>\#(…)</c>)
    /// included. <paramref name="text"/>'s length when the literal does not close.
    /// </summary>
    public static int SkipStringLiteral(string text, int start)
    {
        var i = start;
        var hashes = 0;
        while (i < text.Length && text[i] == '#')
        {
            hashes++;
            i++;
        }
        var multiLine = string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0;
        var quotes = multiLine ? 3 : 1;
        i += quotes;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && TryEscapeEnd(text, i, hashes) is { } escapeEnd)
            {
                i = escapeEnd;
                continue;
            }
            if (c == '"' && string.CompareOrdinal(text, i, new string('"', quotes), 0, quotes) == 0)
            {
                var end = i + quotes;
                var closingHashes = 0;
                while (closingHashes < hashes && end + closingHashes < text.Length && text[end + closingHashes] == '#')
                    closingHashes++;
                if (closingHashes == hashes)
                    return end + hashes;
            }
            i++;
        }
        return text.Length;
    }

    // Past the escape at the backslash `start` of a literal opened with `hashes` hashes: the
    // delimiter is the backslash and as many hashes (`\`, `\#`), and an interpolation (`\(…)`,
    // `\#(…)`) runs to its closing parenthesis, string literals inside it included. Null when the
    // backslash is plain text (a raw string's `\` without its hashes).
    private static int? TryEscapeEnd(string text, int start, int hashes)
    {
        var i = start + 1;
        for (var run = 0; run < hashes; run++, i++)
        {
            if (i >= text.Length || text[i] != '#')
                return null;
        }
        if (i >= text.Length)
            return text.Length;
        if (text[i] != '(')
            return i + 1;
        var close = ClosingParen(text, i);
        return close < 0 ? text.Length : close + 1;
    }

    /// <summary>
    /// The index of the <c>)</c> that closes the <c>(</c> at <paramref name="open"/>, or -1 when it
    /// does not close. String literals neither open nor close a parenthesis.
    /// </summary>
    public static int ClosingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (IsStringLiteralStart(text, i))
            {
                i = SkipStringLiteral(text, i) - 1;
                continue;
            }
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')' && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The bounds of the parameter clause of the declaration <paramref name="declaration"/>: the
    /// clause after the <c>func</c> name, <c>init</c> or <c>subscript</c> and any generic parameter
    /// clause, past the attributes the line carries (<c>@available(…)</c>, <c>@_spi(…)</c>). Null
    /// when there is none or it does not close.
    /// </summary>
    public static (int Open, int Close)? Find(string declaration)
    {
        var keyword = DeclarationKeyword.Match(declaration, SkipAttributes(declaration));
        if (!keyword.Success)
            return null;
        var i = keyword.Index + keyword.Length;
        while (i < declaration.Length && char.IsWhiteSpace(declaration[i]))
            i++;
        if (i < declaration.Length && declaration[i] == '<')
        {
            var angle = 0;
            for (; i < declaration.Length; i++)
            {
                if (declaration[i] == '<')
                    angle++;
                else if (declaration[i] == '>' && declaration[i - 1] != '-' && --angle == 0)
                {
                    i++;
                    break;
                }
            }
            while (i < declaration.Length && char.IsWhiteSpace(declaration[i]))
                i++;
        }
        if (i >= declaration.Length || declaration[i] != '(')
            return null;
        var close = ClosingParen(declaration, i);
        return close < 0 ? null : (i, close);
    }

    /// <summary>
    /// The text between the parameter clause and the return arrow or body: the declaration's
    /// effects (<c>async</c>, <c>throws</c>, <c>rethrows</c>, a typed <c>throws(E)</c>). Null when
    /// the clause cannot be read.
    /// </summary>
    public static string? Effects(string declaration)
    {
        if (Find(declaration) is not { } clause)
            return null;
        var after = declaration[(clause.Close + 1)..];
        var end = after.Length;
        var arrow = SwiftTypeListText.IndexOfTopLevelArrow(after);
        if (arrow >= 0)
            end = arrow;
        var brace = after.IndexOf('{');
        if (brace >= 0)
            end = Math.Min(end, brace);
        return after[..end];
    }

    // Past the leading attributes, each `@name` with its argument clause.
    private static int SkipAttributes(string declaration)
    {
        var i = 0;
        while (true)
        {
            while (i < declaration.Length && char.IsWhiteSpace(declaration[i]))
                i++;
            if (i >= declaration.Length || declaration[i] != '@')
                return i;
            i++;
            while (i < declaration.Length && (char.IsLetterOrDigit(declaration[i]) || declaration[i] is '_' or '.' or ':'))
                i++;
            if (i < declaration.Length && declaration[i] == '(')
            {
                var close = ClosingParen(declaration, i);
                if (close < 0)
                    return declaration.Length;
                i = close + 1;
            }
        }
    }

    // `func` and its name (an identifier, escaped or not, or an operator), `init` with its
    // failability, or `subscript`.
    private static readonly Regex DeclarationKeyword = new(
        @"(?<![\w`])(?:func\s+(?:`[^`]+`|[\p{L}_][\w]*|[/=\-+!*%<>&|^~?.]+)|init[?!]?|subscript)(?![\w`])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
