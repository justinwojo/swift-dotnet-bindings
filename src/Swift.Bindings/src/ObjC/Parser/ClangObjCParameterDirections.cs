// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Joins Clang's canonical declaration print to its JSON by native declaration identity. JSON
/// omits ObjC direction qualifiers; -ast-print preserves their compiler-expanded semantics,
/// including macros. This parser reads only container names, selector pieces and the leading
/// qualifiers of balanced parameter types, never interprets C types or source macros.
/// </summary>
internal sealed class ClangObjCParameterDirections
{
    readonly Dictionary<string, ObjCParameterDirection[]> methods = new(StringComparer.Ordinal);
    readonly bool supplied;
    int requestedMethods;
    int joinedMethods;
    string? firstUnmatchedMethod;

    internal ClangObjCParameterDirections(string? declarations)
    {
        supplied = declarations != null;
        if (declarations == null) return;
        string? container = null;
        var declaration = new StringBuilder();
        using var reader = new StringReader(declarations);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("@end", StringComparison.Ordinal)) { container = null; declaration.Clear(); continue; }
            if (line.StartsWith("@interface ", StringComparison.Ordinal) || line.StartsWith("@protocol ", StringComparison.Ordinal))
            {
                container = ParseContainer(line);
                declaration.Clear();
                continue;
            }
            if (container == null) continue;
            if (declaration.Length == 0 && !line.StartsWith("+ ", StringComparison.Ordinal) && !line.StartsWith("- ", StringComparison.Ordinal)) continue;
            declaration.Append(' ').Append(line);
            if (!line.EndsWith(';')) continue;
            if (ParseMethod(declaration.ToString().Trim(), out var selector, out var instance, out var directions))
            {
                var key = Key(container, selector, instance);
                if (methods.TryGetValue(key, out var previous) && !previous.SequenceEqual(directions))
                    methods[key] = Enumerable.Repeat(ObjCParameterDirection.Unknown, Math.Max(previous.Length, directions.Length)).ToArray();
                else
                    methods.TryAdd(key, directions);
            }
            declaration.Clear();
        }
    }

    internal ObjCParameterDirection[] Get(JsonElement container, string selector, bool instance, int count)
    {
        var kind = container.GetProperty("kind").GetString();
        var name = container.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string identity;
        if (kind == "ObjCCategoryDecl")
        {
            var receiver = container.GetProperty("interface").GetProperty("name").GetString();
            identity = $"category:{receiver}:{name}";
        }
        else
            identity = $"{(kind == "ObjCProtocolDecl" ? "protocol" : "class")}:{name}";
        var key = Key(identity, selector, instance);
        requestedMethods++;
        if (methods.TryGetValue(key, out var facts) && facts.Length == count)
        {
            joinedMethods++;
            return facts;
        }
        firstUnmatchedMethod ??= key;
        // Absent legacy sidecar is distinct from a supplied compiler print we failed to join.
        return Enumerable.Repeat(supplied ? ObjCParameterDirection.Unknown : ObjCParameterDirection.Unspecified, count).ToArray();
    }

    // Validate against declarations actually parsed from this framework, not all containers in
    // the compiler print (which can contain thousands of unrelated SDK declarations). A C-only
    // or property-only framework requests no method facts and may legitimately have empty print.
    internal void ValidateAcquisition(string moduleName)
    {
        if (supplied && requestedMethods > 0 && joinedMethods == 0)
            throw new InvalidOperationException(
                $"Clang canonical declaration acquisition failed for module '{moduleName}': " +
                $"supplied -ast-print output joined none of {requestedMethods} Objective-C method declarations " +
                $"(first unmatched: {firstUnmatchedMethod}). Refusing to treat a failed compiler fact source " +
                "as ordinary unsupported members. Check the canonical output and declaration identities.");
    }

    static string Key(string container, string selector, bool instance) => $"{container}|{(instance ? '-' : '+')}|{selector}";

    static string? ParseContainer(string text)
    {
        if (text.EndsWith(';')) return null; // Forward declaration.
        var match = Regex.Match(text, "^@(interface|protocol)\\s+([A-Za-z_][A-Za-z_0-9]*)");
        if (!match.Success) return null;
        var name = match.Groups[2].Value;
        if (match.Groups[1].Value == "protocol") return $"protocol:{name}";
        var pos = match.Length;
        SkipSpace(text, ref pos);
        if (pos < text.Length && text[pos] == '<')
        {
            var depth = 0;
            do { var c = text[pos++]; if (c == '<') depth++; else if (c == '>') depth--; } while (pos < text.Length && depth > 0);
            SkipSpace(text, ref pos);
        }
        if (pos < text.Length && text[pos] == '(')
        {
            var end = text.IndexOf(')', pos + 1);
            if (end < 0) return null;
            return $"category:{name}:{text[(pos + 1)..end].Trim()}";
        }
        return $"class:{name}";
    }

    static bool ParseMethod(string text, out string selector, out bool instance, out ObjCParameterDirection[] directions)
    {
        selector = ""; instance = text[0] == '-'; directions = [];
        var pos = 1;
        if (!ReadParenthesized(text, ref pos, out _)) return false;
        var pieces = new StringBuilder();
        var result = new List<ObjCParameterDirection>();
        while (pos < text.Length)
        {
            var name = ReadIdentifier(text, ref pos);
            if (name.Length == 0) break;
            SkipSpace(text, ref pos);
            if (pos >= text.Length || text[pos] != ':')
            {
                if (pieces.Length == 0) pieces.Append(name);
                break;
            }
            pieces.Append(name).Append(':'); pos++;
            if (!ReadParenthesized(text, ref pos, out var type)) return false;
            var qualifier = Regex.Match(type.TrimStart(), "^(inout|in|out)\\b").Value;
            result.Add(qualifier switch { "in" => ObjCParameterDirection.In, "out" => ObjCParameterDirection.Out, "inout" => ObjCParameterDirection.InOut, _ => ObjCParameterDirection.Unspecified });
            if (ReadIdentifier(text, ref pos).Length == 0) return false;
        }
        selector = pieces.ToString(); directions = result.ToArray();
        return selector.Length > 0;
    }

    static bool ReadParenthesized(string text, ref int pos, out string content)
    {
        content = ""; SkipSpace(text, ref pos);
        if (pos >= text.Length || text[pos] != '(') return false;
        var begin = ++pos; var depth = 1; char quote = '\0'; bool escaped = false;
        for (; pos < text.Length; pos++)
        {
            var c = text[pos];
            if (quote != '\0') { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) { content = text[begin..pos]; pos++; return true; }
        }
        return false;
    }

    static string ReadIdentifier(string text, ref int pos)
    {
        SkipSpace(text, ref pos); var begin = pos;
        while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
        return text[begin..pos];
    }
    static void SkipSpace(string text, ref int pos) { while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++; }
}
