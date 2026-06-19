// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Normalizes a member's parameter-type list into a stable disambiguation suffix used
/// to distinguish overloads that share the same Swift <c>printedName</c>.
/// <para/>
/// Background: the public <c>swiftinterface</c> / ABI JSON pipeline keys per-member
/// supplementary facts (availability, parameter names, …) by <c>"Type.printedName"</c>.
/// <c>printedName</c> only carries call-site labels — not parameter types — so two
/// overloads like <c>data(for url: URL)</c> and <c>data(for request: ImageRequest)</c>
/// collide on the bare key. The collision causes spurious-broadcast bugs (Family-F-1
/// multi-overload image pipeline) and AddRange version-set merging (Family-F-4 StoreKit2). Appending a
/// normalized parameter-type tail to the key lets producer + consumer agree on which
/// overload owns which annotations.
/// <para/>
/// The normalization needs to be deterministic across <em>two</em> input sources:
/// the SwiftSyntax host (<c>FunctionParameterSyntax</c> nodes serialised to JSON) and the
/// ABI JSON parser (<c>node.Children</c> with their <c>printedName</c>). Every input is
/// reduced
/// to the same canonical tail by:
/// <list type="number">
/// <item>Stripping ownership / opaque-type modifiers (<c>inout</c>, <c>borrowing</c>,
///       <c>consuming</c>, <c>some</c>, <c>any</c>, <c>__owned</c>, <c>__shared</c>).</item>
/// <item>Stripping default-value tails (<c>= ...</c>).</item>
/// <item>Stripping trailing optionality markers (<c>?</c> / <c>!</c>).</item>
/// <item>Folding collection sugar to nominal generic form: <c>[T]</c> becomes
///       <c>Array&lt;T&gt;</c>, <c>[K: V]</c> becomes <c>Dictionary&lt;K,V&gt;</c>.
///       Without this step, the swiftinterface producer (which often prints
///       sugar) and the ABI consumer (which can print either form) would not
///       agree on the disamb suffix.</item>
/// <item>Recursively normalizing generic argument lists (<c>&lt;...&gt;</c>) — the
///       outer head and each comma-separated argument are normalized, then
///       reassembled as <c>OuterTail&lt;arg1Tail,arg2Tail,...&gt;</c>. Stripping
///       the generics outright would collapse distinct overloads like
///       <c>func f(_ x: Array&lt;Int&gt;)</c> and <c>func f(_ x: Array&lt;String&gt;)</c>
///       to the same disamb signature and reintroduce the Family-F broadcast bug.</item>
/// <item>Taking the last <c>.</c>-segment (drops module / nested-type qualifiers).</item>
/// <item>Stripping backticks.</item>
/// </list>
/// Producers / consumers join the per-parameter tails with <c>,</c>. An empty list
/// produces an empty string (no-parameter members like vars / enum cases never need
/// disambiguation).
/// <para/>
/// <b>Home + single-sourcing (Finding 46).</b> This type lives in the unified
/// type-string grammar library (<c>Model/TypeSpecParsing/</c>) alongside
/// <see cref="TypeSpecParser"/> rather than as a stand-alone parser component. The
/// parameter-clause split now delegates to the shared
/// <c>SwiftTypeListText.SplitTopLevelParameters</c> (the same arrow-guarded splitter
/// every other <c>SplitParameters</c> caller uses — Finding 49 consolidation), so
/// there is one clause-splitting grammar.
/// <para/>
/// <b>Load-bearing cross-language parity residual — do NOT "simplify" this.</b>
/// The <em>core</em> reduction below (the ownership/optional/variadic pre-strips,
/// <see cref="CanonicalizeCollectionSugar"/>, the first-<c>&lt;</c> peel, the
/// generic-argument splitter <see cref="SplitGenericArgsTopLevel"/>, the no-space
/// <c>","</c> rejoin and last-dot rule) is a hand-maintained <b>parallel
/// implementation</b> of <c>AvailabilityWalker.normalizeParamType</c> in the Swift
/// SwiftSyntax tool (<c>tools/SwiftInterfaceParser/.../AvailabilityWalker.swift</c>).
/// The contract is that the two emit <b>byte-identical output keys on every input that
/// actually reaches them</b> — not that the two are character-for-character identical:
/// this C# side carries a variadic <c>...</c> pre-strip and quoted-string tracking in
/// <see cref="SplitGenericArgsTopLevel"/> that the Swift side omits, because the Swift
/// producer feeds <em>structured</em> <c>param.type</c> text that never carries a
/// trailing <c>...</c> or a string-literal-bearing type. Those extra C# defenses are
/// no-ops on the inputs the Swift side sees, so the keys still converge. The Swift
/// producer cannot call into this C#, so the two are duplicated by hand and must emit
/// byte-identical keys or availability silently detaches from its member. That mirror
/// CANNOT be replaced
/// by routing through <see cref="TypeSpecParser"/>: the structural grammar deliberately
/// reprints differently (space after a generic comma, <c>Int?</c>→<c>Optional&lt;Int&gt;</c>,
/// <c>Void</c>→<c>()</c>, structural closure/tuple parsing, EOF-strictness), which would
/// desync the unchanged Swift side. True identity-anchoring (keying on usr / mangled
/// name instead of a normalized textual signature) would remove this residual entirely,
/// but <c>.swiftinterface</c> carries no usr/mangled name — that is out-of-scope
/// follow-on work. Until then the parity between this C# normalizer and the Swift
/// host's hand-mirrored reimplementation is enforced <b>by test</b>
/// (<c>MemberSignatureNormalizerTests</c>), not by construction. Any edit here MUST be
/// mirrored verbatim in <c>AvailabilityWalker.swift</c> and proven by those tests.
/// </summary>
internal static class MemberSignatureNormalizer
{
    /// <summary>
    /// Reduces a single Swift parameter type fragment to its canonical tail. Accepts
    /// raw Swift text (<c>"Foundation.URL"</c>, <c>"some UIScene"</c>, <c>"Optional&lt;Int&gt;"</c>)
    /// or ABI <c>printedName</c> (<c>"URL"</c>, <c>"Optional&lt;Int&gt;"</c>) — both reduce
    /// to the same canonical form so producer + consumer agree on the disamb suffix.
    /// Generic argument lists are preserved, normalized recursively, so overloads
    /// distinguished only by their generic specialization (<c>Array&lt;Int&gt;</c>
    /// vs <c>Array&lt;String&gt;</c>) keep distinct signatures.
    /// Returns an empty string when the input is empty.
    /// </summary>
    public static string NormalizeParamType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();

        // Strip ownership / opaque-type modifiers. Iterate so chains like
        // `inout some Foo` collapse to `Foo`.
        bool stripped;
        do
        {
            stripped = false;
            foreach (var prefix in _prefixesToStrip)
            {
                if (s.StartsWith(prefix + " "))
                {
                    s = s.Substring(prefix.Length + 1).TrimStart();
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        // Drop default-value tail (= ...). Source-text only — ABI never carries it.
        var equalsIdx = s.IndexOf('=');
        if (equalsIdx > 0)
            s = s.Substring(0, equalsIdx).TrimEnd();

        // Strip trailing variadic ellipsis (`Foo...`, `[Foo]...`). ABI JSON's param
        // `printedName` includes the trailing `...` for variadic parameters, but the
        // swiftinterface producer's `param.type.trimmedDescription` excludes it (the
        // ellipsis is a separate token on FunctionParameterSyntax, not part of the
        // TypeSyntax). Stripping here brings both sides into parity so overloads
        // differing only in variadic-vs-array-of-variadic (e.g. AppShortcutsBuilder
        // `buildBlock(AppShortcut...)` vs `buildBlock([AppShortcut]...)`) compose
        // the same disamb key on producer and consumer.
        if (s.EndsWith("...", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - 3).TrimEnd();

        // Strip trailing optionality markers (`Int?`, `Int!`). Done before the
        // generic split so `Array<Int>?` reduces to `Array<Int>` and recurses.
        while (s.Length > 0 && (s[s.Length - 1] == '?' || s[s.Length - 1] == '!'))
            s = s.Substring(0, s.Length - 1);
        s = s.TrimEnd();

        // Canonicalize collection sugar to nominal generic form:
        //   `[T]`      → `Array<T>`
        //   `[K: V]`   → `Dictionary<K,V>`
        // The swiftinterface producer commonly emits the sugar form while ABI
        // `printedName` may emit either; collapsing both to the nominal form
        // is the only way the per-side keys round-trip byte-equal. Done BEFORE
        // the generic split so the recursive arg-normalization step sees the
        // canonical shape.
        s = CanonicalizeCollectionSugar(s);

        // Split off the outer generic argument list, if any. Use the FIRST `<`
        // and require the string to end with `>`; the inside is normalized
        // recursively so nested generics like `Dictionary<String, Array<Int>>`
        // round-trip cleanly.
        string outer;
        string? argsInner = null;
        int ltIdx = s.IndexOf('<');
        if (ltIdx > 0 && s.Length > 0 && s[s.Length - 1] == '>')
        {
            outer = s.Substring(0, ltIdx);
            argsInner = s.Substring(ltIdx + 1, s.Length - ltIdx - 2);
        }
        else
        {
            outer = s;
        }

        outer = outer.TrimEnd();

        // Take the last `.`-segment of the outer head so module / nested-type
        // qualifiers don't matter (`Foundation.Array` and `Array` collide).
        var lastDot = outer.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < outer.Length)
            outer = outer.Substring(lastDot + 1);

        // Strip backticks on the outer head (`\`class\`` → `class`).
        if (outer.Length >= 2 && outer[0] == '`' && outer[outer.Length - 1] == '`')
            outer = outer.Substring(1, outer.Length - 2);

        if (argsInner is null) return outer;

        // Recursively normalize each comma-split argument, then reassemble with
        // the outer head. The recursion guarantees nested generics + qualifiers
        // collapse to the same canonical shape regardless of input source.
        //
        // MIRROR-LOCKED: this generic-argument split uses SplitGenericArgsTopLevel
        // (unguarded `>`, brackets+strings), NOT the shared arrow-guarded
        // SwiftTypeListText.SplitTopLevelParameters, because AvailabilityWalker.swift's
        // splitTopLevelCommas is the same unguarded splitter and its output must stay
        // byte-equal. Swapping in the arrow-guarded grammar splitter here would change
        // the key for closure-bearing generic args (e.g. `Foo<(A)->B, C>`) on the C#
        // side only and silently desync the Swift producer.
        var parts = new List<string>();
        foreach (var part in SplitGenericArgsTopLevel(argsInner))
            parts.Add(NormalizeParamType(part));
        return outer + "<" + string.Join(",", parts) + ">";
    }

    /// <summary>
    /// Joins the per-parameter normalized tails into a single disambiguation suffix.
    /// Empty list yields empty string. Used to compose the dict key
    /// <c>$"{bareKey}|{sig}"</c>.
    /// </summary>
    public static string BuildSignature(IReadOnlyList<string> rawParamTypes)
    {
        if (rawParamTypes.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < rawParamTypes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(NormalizeParamType(rawParamTypes[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Composes a disambiguated availability key from a bare member key and a
    /// parameter-type signature. Empty signature produces the bare key unchanged.
    /// Both producer (parser) and consumer (ABI) MUST use this helper so the
    /// composed strings compare byte-equal.
    /// </summary>
    public static string ComposeKey(string bareKey, string signature)
    {
        return string.IsNullOrEmpty(signature) ? bareKey : bareKey + "|" + signature;
    }

    private static readonly string[] _prefixesToStrip = new[]
    {
        "inout", "borrowing", "consuming", "some", "any", "__owned", "__shared",
    };

    /// <summary>
    /// Folds Swift collection sugar (<c>[T]</c>, <c>[K: V]</c>) to the equivalent
    /// nominal generic form (<c>Array&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>).
    /// Returns the input unchanged when it isn't a single bracket-enclosed run.
    /// Inner element types are NOT recursively normalized here — that happens
    /// when <see cref="NormalizeParamType"/> recurses into the generic args.
    /// </summary>
    private static string CanonicalizeCollectionSugar(string s)
    {
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return s;

        // Verify the outer brackets enclose the WHOLE string (depth returns to
        // 0 only at the final ']'). Without this, inputs like `[Int][String]`
        // would be misread as a single bracket pair.
        int depth = 0;
        for (int k = 0; k < s.Length; k++)
        {
            var ch = s[k];
            if (ch == '(' || ch == '[' || ch == '<') depth++;
            else if (ch == ')' || ch == ']' || ch == '>')
            {
                depth--;
                if (depth == 0 && k != s.Length - 1) return s;
            }
        }
        if (depth != 0) return s;

        var inner = s.Substring(1, s.Length - 2);

        // Look for a top-level `:` that separates dictionary key/value.
        depth = 0;
        int colonIdx = -1;
        for (int k = 0; k < inner.Length; k++)
        {
            var c = inner[k];
            if (c == '(' || c == '[' || c == '<') depth++;
            else if (c == ')' || c == ']' || c == '>') depth--;
            else if (c == ':' && depth == 0) { colonIdx = k; break; }
        }
        if (colonIdx < 0) return "Array<" + inner.Trim() + ">";

        var key = inner.Substring(0, colonIdx).Trim();
        var val = inner.Substring(colonIdx + 1).Trim();
        return "Dictionary<" + key + "," + val + ">";
    }

    /// <summary>
    /// Extracts the raw per-parameter type strings from the parameter-clause section
    /// of a Swift function/init signature. The input <paramref name="paramClauseText"/>
    /// is the substring between the matched outer parens of the parameter clause —
    /// caller responsibility to identify those bounds.
    /// <para/>
    /// Splits on top-level commas via the shared <c>SwiftTypeListText.SplitTopLevelParameters</c>
    /// grammar splitter (the same arrow-guarded splitter every other <c>SplitParameters</c>
    /// caller uses — Finding 49/46 consolidation), then for each segment extracts the
    /// substring AFTER the LAST <c>:</c> whose colon is at depth 0 (the type sits after the
    /// final label colon — <c>label1 internalName: Type</c>). Returns an empty list if the
    /// clause is empty or malformed.
    /// <para/>
    /// This is the one piece of the normalizer that is safe to route through the unified
    /// grammar: clause-splitting is C#-only (the SwiftSyntax producer and the ABI consumer
    /// both walk <em>structured</em> parameters and never string-split a clause), so it has
    /// no Swift mirror to desync. The shared splitter is byte-identical to the former private
    /// splitter on every current input and additionally guards the closure return arrow, which
    /// only matters for the latent shape <c>f(cb: (A)->B, x: Int)</c> — where guarding makes
    /// this string-splitting path agree with the structured SwiftSyntax producer instead of
    /// mis-merging.
    /// </summary>
    public static List<string> ExtractParamTypesFromSwiftClause(string paramClauseText)
    {
        var types = new List<string>();
        if (string.IsNullOrWhiteSpace(paramClauseText)) return types;

        foreach (var segment in SwiftTypeListText.SplitTopLevelParameters(paramClauseText))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;

            // The TYPE follows the last top-level colon. `label internalName: Type`
            // has one colon at depth 0; nested generic colons (rare) sit inside `<>`
            // and are filtered out by depth tracking.
            var lastColon = FindLastTopLevelColon(seg);
            if (lastColon < 0)
            {
                // No colon — closure-style anonymous parameter type (`(Int) -> Void`).
                // Treat the whole segment as the type.
                types.Add(seg);
                continue;
            }
            var type = seg.Substring(lastColon + 1).Trim();
            // Drop a default-value tail if the colon found was followed by `Type = expr`.
            var eq = type.IndexOf('=');
            if (eq > 0) type = type.Substring(0, eq).Trim();
            types.Add(type);
        }

        return types;
    }

    /// <summary>
    /// Splits a generic-argument list on top-level commas, tracking <c>()</c>/<c>[]</c>/<c>&lt;&gt;</c>
    /// and quoted strings. <b>Intentionally unguarded</b> on the closure return arrow: a
    /// <c>&gt;</c> always decrements depth, including the <c>&gt;</c> of a <c>-&gt;</c>.
    /// <para/>
    /// <b>MIRROR-LOCKED to <c>AvailabilityWalker.splitTopLevelCommas</c></b> (the Swift
    /// SwiftSyntax producer). This is deliberately NOT the shared, arrow-guarded
    /// <c>SwiftTypeListText.SplitTopLevelParameters</c>: the Swift mirror uses the same
    /// unguarded split for generic args, so the two must stay byte-equal or the
    /// availability key the Swift producer stages and the key the C# consumer looks up
    /// diverge for closure-bearing generic args (e.g. <c>Foo&lt;(A)-&gt;B, C&gt;</c>) and
    /// availability silently detaches. Renamed (was <c>SplitTopLevelCommas</c>) to avoid
    /// confusion with the angle-only <c>SwiftTypeListText.SplitTopLevelCommas</c>, which is
    /// a different splitter. Do not "consolidate" this without changing the Swift mirror in
    /// lockstep and re-proving the cross-producer parity corpus.
    /// </summary>
    private static IEnumerable<string> SplitGenericArgsTopLevel(string text)
    {
        int depth = 0;
        int start = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;
            if (c == '(' || c == '[' || c == '<') depth++;
            else if (c == ')' || c == ']' || c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text.Substring(start, i - start);
                start = i + 1;
            }
        }
        if (start <= text.Length)
            yield return text.Substring(start);
    }

    private static int FindLastTopLevelColon(string text)
    {
        int depth = 0;
        bool inString = false;
        int lastTopLevelColon = -1;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;
            if (c == '(' || c == '[' || c == '<') depth++;
            else if (c == ')' || c == ']' || c == '>') depth--;
            else if (c == ':' && depth == 0)
                lastTopLevelColon = i;
        }
        return lastTopLevelColon;
    }
}
