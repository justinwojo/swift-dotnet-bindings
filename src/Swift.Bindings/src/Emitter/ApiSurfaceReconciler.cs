// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// Checks that every API-manifest entry names a member that actually appears in the emitted C#.
///
/// The manifest (and the api-surface doc rendered from it) is a contract document: a consumer reads
/// it to learn what they can call. Its entries are accumulated at emission chokepoints from the
/// declared model, while the member itself is written later by whichever emitter claims it — and
/// those emitters legitimately reshape what they write (a constructor emits under the type's name, a
/// failable init becomes a static factory with a trailing <c>out</c>, the existential-bypass and
/// metatype-array bridges rewrite the parameter list). Every such reshape is a chance for the
/// document to describe a member that was never emitted, and nothing about a phantom entry breaks
/// the build: the C# still compiles and the tests still pass, so the lie only surfaces when a
/// consumer tries to call what the document promised.
///
/// So the check runs on the real render, right after the files are written, and fails the generator
/// on any discrepancy. It is deliberately permissive about what counts as "present" — a member name
/// paired with an argument count, matched anywhere in the comment-and-literal-stripped text —
/// because the goal is to catch entries with no plausible emitted counterpart at all, not to
/// re-implement C# parsing. A permissive matcher can miss a phantom; it cannot invent one.
///
/// Matching is by <b>supply</b>, not by mere presence: each occurrence of a shape in the emitted
/// text can be claimed by at most one manifest entry, so N entries sharing a shape need N
/// occurrences. That is sound in exactly the direction that matters. The matchers count call sites
/// and indexed accesses as well as declarations, so occurrences ≥ declarations always; a shape can
/// therefore only run short when the emitted text really does declare fewer members than the
/// manifest claims, which is a genuine phantom. Being over-supplied, by contrast, just means a
/// phantom goes unreported — the safe direction for a hard generator error.
///
/// The one thing it deliberately does NOT do is scope matching to the entry's containing type: a
/// key's parent path is dropped and the shape is looked up module-wide, so an entry on one type can
/// be reconciled by a same-shaped member of another. Recovering the containing type would mean
/// tracking the emitted brace structure and assuming a member is always written inside the C# type
/// its Swift parent maps to — an assumption several emitters legitimately break (a member can be
/// written into a proxy, a companion static class, or an extension container). Getting that wrong
/// fails the generator on a correct binding, which is far worse than the miss it would close.
///
/// Argument counting is intentionally naive in the same way on both sides: it tracks only round and
/// square bracket depth, so a comma inside a generic type argument counts as a separator. That
/// inflates the count identically for the manifest key and for the emitted declaration it is
/// compared against, which is all the comparison needs.
/// </summary>
internal static class ApiSurfaceReconciler
{
    /// <summary>
    /// Matches a callable occurrence: an identifier (optionally followed by generic arguments)
    /// immediately preceding an open parenthesis. Deliberately matches call sites as well as
    /// declarations — see the permissiveness note on the class.
    /// </summary>
    private static readonly Regex CallableRegex =
        new(@"(\w+)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);

    /// <summary>Matches an indexer declaration or indexed access: <c>this[</c>.</summary>
    private static readonly Regex IndexerRegex = new(@"\bthis\s*\[", RegexOptions.Compiled);

    /// <summary>
    /// Reconciles <paramref name="manifestEntries"/> against <paramref name="emittedCSharp"/>.
    /// Returns the manifest keys with no plausible emitted counterpart left to claim, in manifest
    /// order — which is the manifest's own Ordinal sort, so the report is deterministic when
    /// several entries share an under-supplied shape.
    /// </summary>
    internal static IReadOnlyList<string> FindUnreconciledEntries(
        IEnumerable<string> manifestEntries, string emittedCSharp)
    {
        var stripped = StripCommentsAndLiterals(emittedCSharp);
        var callableSupply = IndexCallables(stripped);
        var indexerSupply = IndexIndexers(stripped);
        // Filled on demand: scanning for every property name up front would walk the module text
        // once per name, and most modules reference only a fraction of them.
        var propertySupply = new Dictionary<string, int>(StringComparer.Ordinal);

        var unreconciled = new List<string>();
        foreach (var key in manifestEntries)
        {
            if (!TryClaim(key, stripped, callableSupply, indexerSupply, propertySupply))
                unreconciled.Add(key);
        }
        return unreconciled;
    }

    /// <summary>
    /// Reconciles the module's manifest against its emitted C# and throws when any entry names a
    /// member the emitted text has no counterpart for.
    /// </summary>
    internal static void Verify(string moduleName, ModuleEmissionContext emissionContext, string emittedCSharp)
    {
        var entries = emissionContext.ApiManifestEntries;
        if (entries.Count == 0)
            return;

        var unreconciled = FindUnreconciledEntries(entries.Keys, emittedCSharp);
        if (unreconciled.Count > 0)
            throw new ApiSurfaceReconciliationException(moduleName, unreconciled);
    }

    /// <summary>
    /// Claims one occurrence of <paramref name="key"/>'s shape from the emitted text, returning
    /// false when the shape has none left. Mutates the supply maps — see the class note on why
    /// matching consumes rather than merely tests.
    /// </summary>
    private static bool TryClaim(string key, string stripped,
        Dictionary<string, Dictionary<int, int>> callableSupply, Dictionary<int, int> indexerSupply,
        Dictionary<string, int> propertySupply)
    {
        var member = MemberPortion(key);

        if (member.StartsWith("this[", StringComparison.Ordinal))
            return Claim(indexerSupply, CountArguments(member, member.IndexOf('[')));

        int paren = member.IndexOf('(');
        if (paren < 0)
        {
            // A property: no parameter list. Require the name to appear where a property body
            // begins, so an incidental mention (a call argument, a local) does not reconcile it.
            if (!propertySupply.ContainsKey(member))
            {
                propertySupply[member] =
                    Regex.Matches(stripped, @"\b" + Regex.Escape(member) + @"\s*(?:\{|=>)").Count;
            }
            return Claim(propertySupply, member);
        }

        string name = member.Substring(0, paren).Trim();
        return callableSupply.TryGetValue(name, out var arities)
            && Claim(arities, CountArguments(member, paren));
    }

    /// <summary>Takes one occurrence of <paramref name="shape"/> from <paramref name="supply"/>.</summary>
    private static bool Claim<TShape>(Dictionary<TShape, int> supply, TShape shape) where TShape : notnull
    {
        if (!supply.TryGetValue(shape, out int remaining) || remaining <= 0)
            return false;
        supply[shape] = remaining - 1;
        return true;
    }

    /// <summary>
    /// The member portion of a manifest key, with any trailing generic-arity marker dropped: it
    /// qualifies the member, not the shape being matched.
    /// </summary>
    internal static string MemberPortion(string key)
    {
        var member = ModuleEmissionContext.SplitApiManifestKey(key).Member;
        int tick = member.LastIndexOf('`');
        return tick >= 0 && tick > member.LastIndexOf(')') && tick > member.LastIndexOf(']')
            ? member.Substring(0, tick)
            : member;
    }

    /// <summary>
    /// Number of top-level arguments between the opener at <paramref name="openIndex"/> and its
    /// matching closer. Depth tracks round and square brackets only; see the class note on why
    /// generic commas are counted rather than skipped.
    /// </summary>
    internal static int CountArguments(string text, int openIndex)
    {
        int depth = 0;
        int commas = 0;
        bool sawContent = false;
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[') { depth++; continue; }
            if (c is ')' or ']')
            {
                depth--;
                if (depth == 0)
                    return sawContent ? commas + 1 : 0;
                continue;
            }
            if (depth == 1 && c == ',') { commas++; continue; }
            if (depth >= 1 && !char.IsWhiteSpace(c)) sawContent = true;
        }
        return sawContent ? commas + 1 : 0;
    }

    /// <summary>Occurrences of each callable shape in the emitted text: name → arity → count.</summary>
    private static Dictionary<string, Dictionary<int, int>> IndexCallables(string stripped)
    {
        var index = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (Match match in CallableRegex.Matches(stripped))
        {
            // The regex ends on the '(' itself, so its last character is the opener.
            int open = match.Index + match.Length - 1;
            if (!index.TryGetValue(match.Groups[1].Value, out var arities))
                index[match.Groups[1].Value] = arities = new Dictionary<int, int>();
            Add(arities, CountArguments(stripped, open));
        }
        return index;
    }

    /// <summary>Occurrences of each indexer shape in the emitted text: arity → count.</summary>
    private static Dictionary<int, int> IndexIndexers(string stripped)
    {
        var arities = new Dictionary<int, int>();
        foreach (Match match in IndexerRegex.Matches(stripped))
            Add(arities, CountArguments(stripped, match.Index + match.Length - 1));
        return arities;
    }

    private static void Add<TShape>(Dictionary<TShape, int> supply, TShape shape) where TShape : notnull =>
        supply[shape] = supply.TryGetValue(shape, out int count) ? count + 1 : 1;

    /// <summary>
    /// Blanks out comments and string/char literals. Offsets are not preserved — the matchers only
    /// need a text in which a member name mentioned in an XML doc comment, or inside a string
    /// literal, cannot masquerade as an emitted declaration.
    /// </summary>
    internal static string StripCommentsAndLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                sb.Append(' ');
                continue;
            }
            if (c == '"')
            {
                // Verbatim and raw string literals both start with a quote once the '@' / extra
                // '"' prefix is consumed; treating any run up to the next unescaped quote as opaque
                // is enough, because the goal is only to stop literal text from matching.
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;
                sb.Append(' ');
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Thrown when the API manifest records a member the emitted C# has no counterpart for. A generator
/// invariant failure, never auto-resolved: the emitted binding and the document that describes it
/// disagree, and shipping either one silently is worse than failing the build.
/// </summary>
public sealed class ApiSurfaceReconciliationException : Exception
{
    private const int MaxReported = 25;

    public ApiSurfaceReconciliationException(string moduleName, IReadOnlyList<string> unreconciledEntries)
        : base(BuildMessage(moduleName, unreconciledEntries))
    {
        ModuleName = moduleName;
        UnreconciledEntries = unreconciledEntries;
    }

    public string ModuleName { get; }

    public IReadOnlyList<string> UnreconciledEntries { get; }

    private static string BuildMessage(string moduleName, IReadOnlyList<string> entries)
    {
        var sb = new StringBuilder();
        sb.Append($"API surface reconciliation failed for module '{moduleName}': ")
          .Append(entries.Count)
          .Append(entries.Count == 1 ? " manifest entry names a member" : " manifest entries name members")
          .Append(" the emitted C# has no distinct counterpart for. Every manifest entry must name an emitted ")
          .Append("member — an emitter that reshapes the name or parameter list it writes must record ")
          .Append("what it wrote via ModuleEmissionContext.RecordEmittedApiShape.");
        foreach (var entry in entries.Take(MaxReported))
            sb.Append("\n  - ").Append(entry);
        if (entries.Count > MaxReported)
            sb.Append($"\n  ... and {entries.Count - MaxReported} more.");
        return sb.ToString();
    }
}
