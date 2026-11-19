// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Canonicalizes Swift 6.4 module selectors in <c>.swiftinterface</c> text to the dotted
/// spelling the ABI JSON and every earlier compiler use.
/// </summary>
/// <remarks>
/// <para>
/// From Swift 6.4 (Xcode 27) the compiler prints every qualified name in a module interface
/// with module selectors, by default and for every module, not only for shadowed names:
/// <c>extension AppIntents::EntityIdentifier : AppIntents::_IntentValue</c>, and on each
/// component of a nested name, <c>Kit::Outer.Kit::Inner</c>. The ABI JSON the same compiler
/// writes keeps dots (<c>Kit.Outer.Inner</c>). Text readers of the interface written against
/// the dotted spelling silently stop matching, so every C# reader that looks for qualified
/// names reads the interface through <see cref="ReadDotted"/> instead of the raw text.
/// </para>
/// <para>
/// The rendering matches the interface-facts helper's (<c>DottedDescription.swift</c> in
/// <c>tools/SwiftInterfaceParser</c>): a selector that leads a name becomes a dotted module
/// prefix; a selector after a <c>.</c> only says which module declares that member, so it is
/// dropped (rendering it as a component would invent a nested type named after the module).
/// String literals and comments are copied unchanged. Text without <c>::</c> (every Swift 6.2
/// interface) is returned as the same instance, so readers behave byte-for-byte as before on
/// those inputs.
/// </para>
/// <para>
/// Not for text handed back to the compiler: module selectors are what disambiguate a module
/// whose name is shadowed by one of its types, and rendering them as dots re-creates the
/// ambiguity (see <c>SwiftWrapperCompiler.PatchSwiftInterface</c>, which rewrites the raw text).
/// </para>
/// </remarks>
internal static class SwiftModuleSelectors
{
    /// <summary>Reads a module interface with its module selectors rendered as dots.</summary>
    public static string ReadDotted(string swiftInterfacePath) => ToDotted(File.ReadAllText(swiftInterfacePath));

    /// <summary>Renders the module selectors in <paramref name="text"/> as dots.</summary>
    public static string ToDotted(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("::", StringComparison.Ordinal))
            return text;

        var sb = new StringBuilder(text.Length);
        // Whether the last token (comments and whitespace aside) is a member-access dot.
        var afterMemberDot = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Comments and string literals keep their text; comments are not tokens.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = BlockCommentEnd(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c == '"' || c == '#' && RawStringHashCount(text, i) > 0)
            {
                var end = StringLiteralEnd(text, i);
                sb.Append(text, i, end - i);
                i = end;
                afterMemberDot = false;
                continue;
            }

            // An identifier (plain or backquoted) at a token start.
            if (IsIdentifierStart(c) && (i == 0 || !IsIdentifierPart(text[i - 1])) || c == '`')
            {
                var end = IdentifierEnd(text, i);
                if (end == i)
                {
                    // An unclosed backquote: copy it and move on.
                    sb.Append(c);
                    i++;
                    afterMemberDot = false;
                    continue;
                }
                if (end + 1 < text.Length && text[end] == ':' && text[end + 1] == ':')
                {
                    // `.Kit::Inner`: the selector names the declaring module of a member, so it
                    // is dropped; a leading selector becomes a dotted module prefix.
                    if (!afterMemberDot)
                        sb.Append(text, i, end - i).Append('.');
                    i = end + 2;
                    afterMemberDot = false;
                    continue;
                }
                sb.Append(text, i, end - i);
                i = end;
                afterMemberDot = false;
                continue;
            }

            afterMemberDot = c == '.' && IsMemberDot(text, i);
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    // End (exclusive) of the identifier starting at `start`, or `start` when none starts there.
    private static int IdentifierEnd(string text, int start)
    {
        if (text[start] == '`')
        {
            var close = text.IndexOf('`', start + 1);
            return close < 0 || text.IndexOf('\n', start, close - start) >= 0 ? start : close + 1;
        }
        var i = start;
        while (i < text.Length && IsIdentifierPart(text[i]))
            i++;
        return i;
    }

    // End (exclusive) of the block comment opening at `start`; Swift block comments nest.
    private static int BlockCommentEnd(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i + 1 < text.Length)
        {
            if (text[i] == '/' && text[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (text[i] == '*' && text[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                    return i;
            }
            else
            {
                i++;
            }
        }
        return text.Length;
    }

    // The number of `#` delimiters of a raw string literal opening at `start`, or 0.
    private static int RawStringHashCount(string text, int start)
    {
        var i = start;
        while (i < text.Length && text[i] == '#')
            i++;
        return i < text.Length && text[i] == '"' ? i - start : 0;
    }

    // End (exclusive) of the string literal opening at `start`: `"…"` or `"""…"""`, each
    // optionally raw (`#"…"#`, `##"""…"""##`). Escapes apply only to non-raw literals; in a raw
    // literal a quote closes it only when followed by the same number of `#`.
    private static int StringLiteralEnd(string text, int start)
    {
        var hashes = text[start] == '#' ? RawStringHashCount(text, start) : 0;
        var open = start + hashes;
        var closer = new string('#', hashes);
        // An escape is a backslash followed by the literal's own `#` delimiters (`\#"` in `#"…"#`).
        var escape = "\\" + closer;
        if (string.CompareOrdinal(text, open, "\"\"\"", 0, 3) == 0)
        {
            var search = open + 3;
            while (true)
            {
                var close = text.IndexOf("\"\"\"" + closer, search, StringComparison.Ordinal);
                if (close < 0)
                    return text.Length;
                var escaped = hashes == 0
                    ? IsEscaped(text, close, open + 3)
                    : close - escape.Length >= open + 3 && string.CompareOrdinal(text, close - escape.Length, escape, 0, escape.Length) == 0;
                if (!escaped)
                    return close + 3 + hashes;
                search = close + 1;
            }
        }
        var i = open + 1;
        while (i < text.Length && text[i] != '\n')
        {
            if (text[i] == '"' && string.CompareOrdinal(text, i + 1, closer, 0, hashes) == 0)
                return i + 1 + hashes;
            if (string.CompareOrdinal(text, i, escape, 0, escape.Length) == 0 && i + escape.Length < text.Length)
            {
                // `\(…)` interpolates code, which may hold string literals of its own.
                i = text[i + escape.Length] == '(' ? InterpolationEnd(text, i + escape.Length) : i + escape.Length + 1;
                continue;
            }
            i++;
        }
        return i;
    }

    // End (exclusive) of the interpolation whose `(` is at `open`: its closing `)`, skipping
    // the string literals and nested parentheses inside it.
    private static int InterpolationEnd(string text, int open)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' || c == '#' && RawStringHashCount(text, i) > 0)
            {
                i = StringLiteralEnd(text, i);
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return i + 1;
            i++;
        }
        return text.Length;
    }

    // Whether the `.` at `index` is a member access rather than part of a dot operator
    // (`...`, `..<`, `.+.`): an operator that holds a dot starts with one, so a dot is an operator
    // character when another operator character follows it, or when the operator characters
    // running back from it hold a dot.
    private static bool IsMemberDot(string text, int index)
    {
        if (index + 1 < text.Length && IsOperatorCharacter(text[index + 1]) && !StartsComment(text, index + 1))
            return false;
        for (var j = index - 1; j >= 0 && IsOperatorCharacter(text[j]); j--)
        {
            // A comment ends here; what it holds is not part of an operator.
            if (text[j] == '/' && j > 0 && text[j - 1] == '*')
                break;
            if (text[j] == '.')
                return false;
        }
        return true;
    }

    // Whether the character at `index` is preceded by an odd run of backslashes after `floor`.
    private static bool IsEscaped(string text, int index, int floor)
    {
        var count = 0;
        for (var i = index - 1; i >= floor && text[i] == '\\'; i--)
            count++;
        return count % 2 == 1;
    }

    private static bool StartsComment(string text, int index) =>
        text[index] == '/' && index + 1 < text.Length && (text[index + 1] == '/' || text[index + 1] == '*');

    private static bool IsOperatorCharacter(char c) => c is '.' or '/' or '=' or '-' or '+' or '!'
        or '*' or '%' or '<' or '>' or '&' or '|' or '^' or '~' or '?';
}
