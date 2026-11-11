// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Rewrites generic parameters that an ABI dump spells by source name (<c>Value</c>,
/// <c>Element</c>) into the depth-and-index spelling (<c>τ_0_0</c>) the rest of the pipeline
/// reads, deciding which names are generic parameters from the generic signature of the
/// declaration that introduces them.
///
/// <para>The two ABI producers the generator consumes spell generic parameters differently. The
/// compiler's ABI descriptor names each by depth and index, printing the source names separately
/// as <c>sugared_genericSig</c>. <c>swift-api-digester -dump-sdk</c>, which is how an Apple SDK
/// framework reaches the generator, prints source names only, in both the signature and every type
/// that uses one. A depth-and-index name is unambiguous; a source name is an ordinary identifier
/// that only its declaration's signature identifies as a generic parameter. Rewriting here, from
/// that signature, lets every later stage treat the two dumps alike rather than guess from a
/// name's spelling, and keeps the source names on the node as <c>sugared_genericSig</c>, exactly
/// where the compiler's dump carries them.</para>
///
/// <para>A declaration's signature lists every enclosing generic parameter first, then its own
/// (<c>&lt;Value, T&gt;</c> for a generic method of <c>Box&lt;Value&gt;</c>). Its own parameters
/// therefore sit one depth below the nearest enclosing signature, and a declaration that adds
/// none (an accessor, a non-generic nested type) shares that depth. Nodes without a signature
/// (a stored property, a type reference) resolve names through the nearest enclosing one.</para>
///
/// <para>Left alone, deliberately:</para>
/// <list type="bullet">
/// <item>Protocol scopes — any signature that begins with <c>Self</c>. Protocol requirements and
/// protocol-extension members are read in their <c>Self</c> spelling throughout the protocol
/// path, and nothing below a protocol scope is rewritten.</item>
/// <item>A dump already in depth-and-index spelling, or any node that already carries a sugared
/// twin — the compiler's own output passes through untouched.</item>
/// <item>A signature that does not extend its enclosing one, or that repeats a name. Neither
/// shape is produced by a well-formed dump; such a subtree keeps its original spelling.</item>
/// </list>
///
/// <para>Within a type's printed spelling a name is replaced only where it stands as a type: not
/// after a <c>.</c> (a member type such as <c>Foo.Value</c>) and not directly before a <c>:</c>
/// (a tuple label such as <c>value:</c>; a dictionary type prints <c>[Key : Value]</c> with a
/// space). Nominal types in both dumps are module-qualified, so a bare identifier in a type is a
/// generic parameter of an enclosing scope.</para>
/// </summary>
public static class GenericParameterDialectNormalizer
{
    private const string DepthIndexPrefix = "τ_";

    /// <summary>
    /// The generic parameters visible at a point in the tree: source name → depth-and-index name.
    /// <see cref="Names"/> keeps the declaration order, which an inner signature must extend.
    /// </summary>
    private sealed record Scope(IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> Map, int Depth);

    /// <summary>
    /// Rewrites every source-named generic parameter in <paramref name="root"/> in place.
    /// Returns the number of declarations whose signature was rewritten.
    /// </summary>
    public static int Normalize(RootNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        int rewritten = 0;
        foreach (var child in root.Children)
            Visit(child, scope: null, ref rewritten);
        return rewritten;
    }

    private static void Visit(Node node, Scope? scope, ref int rewritten)
    {
        if (!string.IsNullOrEmpty(node.GenericSig))
        {
            if (node.GenericSig.Contains(DepthIndexPrefix, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(node.sugared_genericSig))
            {
                // Already in the compiler's spelling. Its descendants are too.
                return;
            }

            var names = ParseParameterNames(node.GenericSig);
            if (names is null || (names.Count > 0 && names[0] == "Self"))
                return;

            var inner = Extend(scope, names);
            if (inner is null)
                return;

            node.sugared_genericSig = node.GenericSig;
            node.GenericSig = Rewrite(node.GenericSig, inner.Map, isSignature: true);
            rewritten++;
            scope = inner;
        }

        if (scope is not null)
        {
            if (IsTypeNode(node))
                node.PrintedName = Rewrite(node.PrintedName, scope.Map, isSignature: false);
            if (node.superclassNames is { Length: > 0 } superclasses)
            {
                for (int i = 0; i < superclasses.Length; i++)
                    superclasses[i] = Rewrite(superclasses[i], scope.Map, isSignature: false);
            }
        }

        foreach (var child in node.Children ?? Enumerable.Empty<Node>())
            Visit(child, scope, ref rewritten);
        foreach (var accessor in node.Accessors ?? Enumerable.Empty<Node>())
            Visit(accessor, scope, ref rewritten);
        foreach (var conformance in node.Conformances ?? Enumerable.Empty<Node>())
            Visit(conformance, scope, ref rewritten);
    }

    private static bool IsTypeNode(Node node) =>
        node.Kind is "TypeNominal" or "TypeFunc" or "TypeNameAlias";

    /// <summary>
    /// The scope a signature listing <paramref name="names"/> introduces below
    /// <paramref name="outer"/>, or null when the signature does not extend the outer one.
    /// </summary>
    private static Scope? Extend(Scope? outer, List<string> names)
    {
        if (names.Count == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            return null;

        var outerNames = outer?.Names ?? Array.Empty<string>();
        if (names.Count < outerNames.Count)
            return null;
        for (int i = 0; i < outerNames.Count; i++)
        {
            if (!string.Equals(names[i], outerNames[i], StringComparison.Ordinal))
                return null;
        }

        if (outer is not null && names.Count == outerNames.Count)
            return outer;

        int depth = outer is null ? 0 : outer.Depth + 1;
        var map = outer is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(outer.Map, StringComparer.Ordinal);
        for (int i = outerNames.Count; i < names.Count; i++)
            map[names[i]] = $"{DepthIndexPrefix}{depth}_{i - outerNames.Count}";
        return new Scope(names, map, depth);
    }

    /// <summary>
    /// The parameter names of a printed generic signature (<c>&lt;Key, Value where Key : P&gt;</c>
    /// → <c>Key</c>, <c>Value</c>), in order. A pack or value parameter's introducer
    /// (<c>each T</c>, <c>let N : Swift.Int</c>) is dropped. Null when the text is not a signature
    /// or a parameter is not a plain identifier.
    /// </summary>
    internal static List<string>? ParseParameterNames(string genericSig)
    {
        var text = genericSig.Trim();
        if (text.Length < 2 || text[0] != '<' || text[^1] != '>')
            return null;
        text = text[1..^1];

        int whereIndex = IndexOfTopLevelWhere(text);
        if (whereIndex >= 0)
            text = text[..whereIndex];

        var names = new List<string>();
        foreach (var raw in SplitTopLevel(text))
        {
            var entry = raw.Trim();
            if (entry.StartsWith("each ", StringComparison.Ordinal))
                entry = entry["each ".Length..].TrimStart();
            else if (entry.StartsWith("let ", StringComparison.Ordinal))
                entry = entry["let ".Length..].TrimStart();
            int colon = entry.IndexOf(':');
            if (colon >= 0)
                entry = entry[..colon].TrimEnd();
            if (!IsIdentifier(entry))
                return null;
            names.Add(entry);
        }
        return names;
    }

    private static int IndexOfTopLevelWhere(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (depth == 0 && c == ' ' && string.CompareOrdinal(text, i, " where ", 0, 7) == 0)
                return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
            return false;
        foreach (char c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Replaces each identifier of <paramref name="text"/> that names a generic parameter in
    /// <paramref name="map"/>. In a printed type, a name directly followed by <c>:</c> is a tuple
    /// label and is kept; in a signature, <c>T : P</c> is a constraint, so the rule does not apply.
    /// </summary>
    internal static string Rewrite(string text, IReadOnlyDictionary<string, string> map, bool isSignature)
    {
        if (string.IsNullOrEmpty(text) || map.Count == 0)
            return text;

        StringBuilder? builder = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (!(char.IsLetter(c) || c == '_'))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;

            if (start > 0 && text[start - 1] == '.')
                continue;
            if (!isSignature && i < text.Length && text[i] == ':')
                continue;
            if (!map.TryGetValue(text.Substring(start, i - start), out var replacement))
                continue;

            builder ??= new StringBuilder(text.Length + 8);
            builder.Append(text, copied, start - copied).Append(replacement);
            copied = i;
        }

        if (builder is null)
            return text;
        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }
}
