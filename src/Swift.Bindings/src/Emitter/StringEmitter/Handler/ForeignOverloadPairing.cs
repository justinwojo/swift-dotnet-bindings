// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// Pairs the members an interface declares on another module's type with the members the ABI lists
/// there, so each listed member accounts for one declared member and no more. Overloads can share a
/// name, labels and staticness and differ only in their types; a listed member pairs first with the
/// declared member whose parameter types read the same, then with any remaining one of its shape.
/// </summary>
internal static class ForeignOverloadPairing
{
    /// <summary>
    /// The declared members no listed member accounts for. Listed members in
    /// <paramref name="claimed"/> already account for another declared member and are skipped; the
    /// ones paired here are added to it.
    /// </summary>
    internal static List<TDeclared> Unpaired<TDeclared, TListed>(
        IReadOnlyList<TDeclared> declared,
        IEnumerable<TListed> listed,
        Func<TDeclared, TListed, bool> sameShape,
        Func<TDeclared, string?> declaredTypes,
        Func<TListed, string?> listedTypes,
        ISet<object>? claimed = null)
        where TListed : class
    {
        var pool = listed.Where(l => claimed is null || !claimed.Contains(l)).ToList();
        var paired = new bool[declared.Count];
        foreach (var byTypes in new[] { true, false })
        {
            for (var i = 0; i < declared.Count && pool.Count > 0; i++)
            {
                if (paired[i])
                    continue;
                var types = byTypes ? Normalize(declaredTypes(declared[i])) : null;
                if (byTypes && types is null)
                    continue;
                var match = pool.FirstOrDefault(l => sameShape(declared[i], l)
                    && (!byTypes || string.Equals(types, Normalize(listedTypes(l)), StringComparison.Ordinal)));
                if (match is null)
                    continue;
                pool.Remove(match);
                claimed?.Add(match);
                paired[i] = true;
            }
        }
        return declared.Where((_, i) => !paired[i]).ToList();
    }

    /// <summary>
    /// The parameter types of the declaration <paramref name="keyword"/> introduces in
    /// <paramref name="signature"/> (<c>func name</c>, <c>init</c>, <c>subscript</c>), comma-separated,
    /// or null when its parameter clause cannot be read.
    /// </summary>
    internal static string? ParameterTypesOf(string signature, string keyword)
    {
        var at = Regex.Match(signature, $@"(?<![\w`]){Regex.Escape(keyword)}(?![\w`])");
        if (!at.Success)
            return null;
        var angle = 0;
        for (var i = at.Index + at.Length; i < signature.Length; i++)
        {
            var c = signature[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(' && angle == 0)
            {
                var clause = CarriedReceiverRows.TypesOfClause(signature, i);
                return clause.Length < 2 ? null : string.Join(", ", ParameterTypes(clause[1..^1]));
            }
        }
        return null;
    }

    /// <summary>Types compared across the two sides: module qualifiers, attributes, ownership and
    /// whitespace dropped.</summary>
    internal static string? Normalize(string? types)
        => types is null
            ? null
            : Whitespace.Replace(
                ProtocolExtensionEmitter.WithoutModuleQualifiers(Ownership.Replace(Attribute.Replace(types, " "), " ")),
                string.Empty);

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
                yield return (colon < 0 ? parameter : parameter[(colon + 1)..]).Trim();
            }
        }
    }

    private static int TopLevelColon(string parameter)
    {
        var depth = 0;
        for (var i = 0; i < parameter.Length; i++)
        {
            var c = parameter[i];
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' || (c == '>' && (i == 0 || parameter[i - 1] != '-'))) depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    private static readonly Regex Attribute = new(@"@\w+(?:\([^)]*\))?", RegexOptions.Compiled);
    private static readonly Regex Ownership = new(@"\b(?:inout|__owned|__shared|borrowing|consuming|sending)\b", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
}
