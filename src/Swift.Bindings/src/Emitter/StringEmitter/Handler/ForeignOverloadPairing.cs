// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// What tells overloads of one name apart once kind, labels and staticness agree: the subscript labels
/// (null where the side does not record them, or where labels belong to the shape instead), the
/// parameter types and the return type, as each side spells them. Parameters are null when they
/// cannot be compared: the clause does not read, or a type is a generic parameter, which the ABI
/// spells by depth and index. A null return is not compared (an initializer's).
/// </summary>
internal sealed record OverloadSignature(string? Labels, IReadOnlyList<string>? Parameters, string? Return);

/// <summary>
/// Pairs the members an interface declares on another module's type with the members the ABI lists
/// there, so each listed member accounts for one declared member and no more. A listed member pairs
/// first with a declared member whose signature reads the same, then with one whose differing types
/// may be typealiases the ABI resolves (<see cref="CarriedReceiverRows.MayBeSpelledAs"/>), and only
/// when a side's types cannot be read, with any remaining one of its shape.
/// </summary>
internal static class ForeignOverloadPairing
{
    /// <summary>
    /// The listed member each declared member stands for, by the declared member's index; a declared
    /// member no listed member accounts for has no entry. Every declared member is paired by one pass
    /// before the next pass starts, so a declaration whose signature reads the same as a listed
    /// member's always takes it before one that only may be it.
    /// </summary>
    internal static Dictionary<int, TListed> Pair<TDeclared, TListed>(
        IReadOnlyList<TDeclared> declared,
        IEnumerable<TListed> listed,
        Func<TDeclared, TListed, bool> sameShape,
        Func<TDeclared, OverloadSignature?> declaredSignature,
        Func<TListed, OverloadSignature?> listedSignature,
        ITypeDatabase typeDatabase)
        where TListed : class
    {
        var pool = listed.ToList();
        var pairs = new Dictionary<int, TListed>();
        var mine = declared.Select(declaredSignature).ToList();
        var pass = new Func<OverloadSignature?, OverloadSignature?, bool>[]
        {
            (d, l) => Comparable(d, l) && SameSignature(d!, l!),
            (d, l) => Comparable(d, l) && MayBeSpelledAs(d!, l!, typeDatabase),
            (d, l) => !Comparable(d, l),
        };
        foreach (var matches in pass)
        {
            for (var i = 0; i < declared.Count && pool.Count > 0; i++)
            {
                if (pairs.ContainsKey(i))
                    continue;
                var match = pool.FirstOrDefault(l => sameShape(declared[i], l) && matches(mine[i], listedSignature(l)));
                if (match is null)
                    continue;
                pool.Remove(match);
                pairs[i] = match;
            }
        }
        return pairs;
    }

    private static bool Comparable(OverloadSignature? declared, OverloadSignature? listed)
        => declared?.Parameters is not null && listed?.Parameters is not null;

    private static bool SameSignature(OverloadSignature declared, OverloadSignature listed)
        => LabelsAgree(declared.Labels, listed.Labels, allowErased: false)
            && declared.Parameters!.Count == listed.Parameters!.Count
            && declared.Parameters.Zip(listed.Parameters).All(p => SameType(p.First, p.Second))
            && (declared.Return is null || listed.Return is null || SameType(declared.Return, listed.Return));

    private static bool MayBeSpelledAs(OverloadSignature declared, OverloadSignature listed, ITypeDatabase typeDatabase)
    {
        bool Type(string d, string l) => SameType(d, l) || CarriedReceiverRows.MayBeSpelledAs(d, l, typeDatabase);
        return LabelsAgree(declared.Labels, listed.Labels, allowErased: true)
            && declared.Parameters!.Count == listed.Parameters!.Count
            && declared.Parameters.Zip(listed.Parameters).All(p => Type(p.First, p.Second))
            && (declared.Return is null || listed.Return is null || Type(declared.Return, listed.Return));
    }

    // A listed subscript the ABI printed without its labels (`subscript(_:)`) may still be a labelled
    // declaration; it pairs with one only once the declarations whose labels agree have paired.
    private static bool LabelsAgree(string? declared, string? listed, bool allowErased)
        => declared is null || listed is null || string.Equals(declared, listed, StringComparison.Ordinal)
            || (allowErased && listed.Split(':', StringSplitOptions.RemoveEmptyEntries).All(l => l == "_"));

    private static bool SameType(string declared, string listed)
        => string.Equals(Normalize(declared), Normalize(listed), StringComparison.Ordinal);

    /// <summary>
    /// The signature of the declaration <paramref name="keyword"/> introduces in
    /// <paramref name="declaration"/> (<c>func</c> with <paramref name="name"/>, <c>init</c>,
    /// <c>subscript</c>): its parameter types and return type, or null when its parameter clause
    /// cannot be read. A method that declares generic parameters of its own compares no types.
    /// </summary>
    internal static OverloadSignature? SignatureOf(string declaration, string keyword, string? name = null, string? labels = null)
    {
        var pattern = keyword == "func"
            ? $@"(?<![\w`])func\s+`?{Regex.Escape(name ?? string.Empty)}`?(?![\w`])"
            : $@"(?<![\w`]){Regex.Escape(keyword)}(?![\w`])";
        var at = Regex.Match(declaration, pattern);
        if (!at.Success)
            return null;
        var angle = 0;
        var ownGenerics = false;
        for (var i = at.Index + at.Length; i < declaration.Length; i++)
        {
            var c = declaration[i];
            if (c == '<') { angle++; ownGenerics = true; }
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(' && angle == 0)
            {
                var clause = CarriedReceiverRows.TypesOfClause(declaration, i);
                var end = ClauseEnd(declaration, i);
                if (clause.Length < 2 || end < 0)
                    return null;
                var returnType = ReturnTypeAfter(declaration, end);
                return new OverloadSignature(
                    labels,
                    ownGenerics ? null : ParameterTypes(clause[1..^1]).ToList(),
                    keyword == "init" ? null : returnType ?? "()");
            }
            else if (angle == 0 && !char.IsWhiteSpace(c) && c is not '?' and not '!')
                return null;
        }
        return null;
    }

    /// <summary>
    /// The signature of a member the ABI lists, from its parsed parameter and return types. Types
    /// that name a generic parameter by depth and index (<c>τ_0_0</c>) do not compare with the
    /// interface's source names, so such a member compares no types.
    /// </summary>
    internal static OverloadSignature ListedSignature(string? labels, IEnumerable<string?> parameters, string? returnType)
    {
        var types = parameters.ToList();
        var comparable = types.All(t => t is not null && !t.Contains('τ'))
            && (returnType is null || !returnType.Contains('τ'));
        return new OverloadSignature(labels, comparable ? types.Select(t => t!).ToList() : null, returnType);
    }

    /// <summary>A type compared across the two sides: sugar spelled out, module qualifiers,
    /// attributes, ownership and whitespace dropped, and every spelling of void made one.</summary>
    internal static string? Normalize(string? type)
    {
        if (type is null)
            return null;
        var text = Ownership.Replace(Attribute.Replace(type, " "), " ").Trim();
        try
        {
            if (TypeSpecParser.Parse(text) is { } parsed)
                text = parsed.ToString();
        }
        catch (Exception)
        {
            // A type the parser does not read is compared as written.
        }
        text = Whitespace.Replace(ProtocolExtensionEmitter.WithoutModuleQualifiers(text), string.Empty);
        return text is "Void" or "()" ? "()" : text;
    }

    // The return type after the parameter clause ending at `end`: effects skipped, up to a `where`
    // clause, a body or the end; null when no arrow follows.
    private static string? ReturnTypeAfter(string declaration, int end)
    {
        var tail = WithoutEffects(declaration[end..]);
        if (!tail.StartsWith("->", StringComparison.Ordinal))
            return null;
        tail = tail[2..];
        var depth = 0;
        for (var i = 0; i < tail.Length; i++)
        {
            var c = tail[i];
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' || (c == '>' && (i == 0 || tail[i - 1] != '-'))) depth--;
            else if (depth == 0 && (c == '{' || (c == 'w' && WhereAt.IsMatch(tail[Math.Max(0, i - 1)..]))))
                return tail[..i].Trim();
        }
        return tail.Trim();
    }

    // The index just past the parameter clause opening at `open` (default values and string literals
    // included, which the clause's types leave out), or -1 when it does not close.
    private static int ClauseEnd(string declaration, int open)
    {
        var depth = 0;
        for (var j = open; j < declaration.Length; j++)
        {
            if (SwiftParameterClause.IsStringLiteralStart(declaration, j))
            {
                j = SwiftParameterClause.SkipStringLiteral(declaration, j) - 1;
                continue;
            }
            var c = declaration[j];
            if (c is '(' or '[' or '<') depth++;
            else if ((c is ')' or ']' || (c == '>' && declaration[j - 1] != '-')) && --depth == 0)
                return j + 1;
        }
        return -1;
    }

    // The text after the effects that open it: `async`, `reasync`, `rethrows`, and `throws` with any
    // typed error, whose type may itself hold parentheses.
    private static string WithoutEffects(string text)
    {
        while (true)
        {
            text = text.TrimStart();
            var effect = Effect.Match(text);
            if (!effect.Success)
                return text;
            text = text[effect.Length..].TrimStart();
            if (effect.Value == "throws" && text.StartsWith('(') && ClauseEnd(text, 0) is var end and > 0)
                text = text[end..];
        }
    }

    // Each parameter's type: the text after its first top-level colon.
    private static IEnumerable<string> ParameterTypes(string clause)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= clause.Length; i++)
        {
            var c = i < clause.Length ? clause[i] : ',';
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' || (c == '>' && (i == 0 || clause[i - 1] != '-'))) depth--;
            else if (c == ',' && depth == 0)
            {
                var parameter = clause[start..i];
                start = i + 1;
                if (parameter.Trim().Length == 0)
                    continue;
                var colon = TopLevelColon(parameter);
                var type = (colon < 0 ? parameter : parameter[(colon + 1)..]).Trim();
                // A default value is not part of the type.
                var assignment = TopLevelAssignment(type);
                yield return (assignment < 0 ? type : type[..assignment]).Trim();
            }
        }
    }

    private static int TopLevelColon(string parameter) => TopLevel(parameter, ':');

    private static int TopLevelAssignment(string type) => TopLevel(type, '=');

    private static int TopLevel(string text, char wanted)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' || (c == '>' && (i == 0 || text[i - 1] != '-'))) depth--;
            else if (c == wanted && depth == 0) return i;
        }
        return -1;
    }

    private static readonly Regex Attribute = new(@"@\w+(?:\([^)]*\))?", RegexOptions.Compiled);
    private static readonly Regex Ownership = new(@"\b(?:inout|__owned|__shared|borrowing|consuming|sending)\b", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Effect = new(@"^(?:async|reasync|rethrows|throws)(?![\w`])", RegexOptions.Compiled);
    private static readonly Regex WhereAt = new(@"^(?:\s)where\b", RegexOptions.Compiled);
}
