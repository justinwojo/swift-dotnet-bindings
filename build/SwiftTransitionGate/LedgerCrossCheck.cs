// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftBindings.TransitionGate;

/// <summary>The transition kinds the ledger and the IL inventory are joined on.</summary>
public enum LedgerTransitionKind
{
    /// <summary>A forward Swift-convention P/Invoke.</summary>
    ForwardPInvoke,

    /// <summary>A P/Invoke of another convention that carries a Swift register type (rule R2).</summary>
    CdeclSpecialPInvoke,

    /// <summary>A Swift-convention <c>[UnmanagedCallersOnly]</c> callback.</summary>
    ReverseCallback,

    /// <summary>An <c>ldftn</c> of a Swift-convention callback: where a closure is constructed.</summary>
    CallbackAddress,

    /// <summary>A forward <c>calli</c> through an <c>unmanaged[Swift]</c> pointer.</summary>
    ForwardCalli,
}

/// <summary>One transition, as either side names it: its kind, its join key and, for a forward transition, whether it is primitive-only.</summary>
public sealed record LedgerJoinItem(LedgerTransitionKind Kind, string Key, bool? Primitive, string Origin);

/// <summary>
/// One ledger entry, as the cross-check reads it from the sidecar JSON.
/// </summary>
public sealed record LedgerEntryView(
    LedgerTransitionKind Kind, string Key, bool? Primitive, string Status, ImmutableArray<string> Reasons, string Origin);

/// <summary>The cross-check's result over one IL assembly and the ledgers of the modules compiled into it.</summary>
public sealed record LedgerCrossCheckResult(
    ImmutableSortedDictionary<LedgerTransitionKind, int> IlSites,
    ImmutableSortedDictionary<LedgerTransitionKind, int> LedgerEntries,
    int PrimitiveMarked,
    ImmutableArray<string> MissingFromLedger,
    ImmutableArray<string> MissingFromIl,
    ImmutableArray<string> CarrierMismatches,
    ImmutableArray<string> Unplanned,
    ImmutableArray<string> UnplannedCdeclSpecial,
    ImmutableArray<string> Incomplete,
    ImmutableArray<string> HandWritten,
    ImmutableArray<string> LedgerIssues,
    ImmutableArray<string> Problems)
{
    /// <summary>
    /// A bijection between the IL's transition sites and the ledger's entries, kind by kind; every
    /// primitive verdict agreeing; every entry planned; no ledger issue; nothing unreadable.
    /// An incomplete entry (facts with an unknown) is reported, not failed: its reasons are the
    /// planner's work list, and the facts it does carry are still sound. So is a C-convention import
    /// that names a Swift register type with no facts behind it: it calls a C entry point the bindings
    /// export themselves, not a Swift function, so there is no Swift call to describe; its presence and
    /// its register types are still joined.
    /// </summary>
    public bool Passed => MissingFromLedger.IsEmpty && MissingFromIl.IsEmpty && CarrierMismatches.IsEmpty
        && Unplanned.IsEmpty && LedgerIssues.IsEmpty && Problems.IsEmpty;
}

/// <summary>
/// Cross-checks the generator's transition ledgers (<c>*.swift-transition-ledger.json</c>, one per
/// module) against the transition gate's IL inventory of the assembly those modules compile into: every
/// transition site the IL has must have exactly one ledger entry and every ledger entry one site
/// (multiplicity counts), the two must agree on which forward transitions are primitive-only, and every
/// entry must carry facts. A transition a producer rendered without recording, or one the ledger's
/// reading of the C# missed, fails it.
/// </summary>
public static class LedgerCrossCheck
{
    /// <summary>The ledger schema this cross-check reads.</summary>
    public const int LedgerSchema = 2;

    public const string LedgerSuffix = ".swift-transition-ledger.json";

    /// <summary>
    /// Cross-checks <paramref name="il"/> against the ledgers in <paramref name="ledgerDirectory"/>
    /// (top level only). Fails closed: no ledger at all is a problem, not an empty pass.
    /// </summary>
    public static LedgerCrossCheckResult CheckDirectory(
        AssemblyTransitionReport il, string ledgerDirectory, IReadOnlyCollection<string>? handWrittenTypes = null)
    {
        var files = Directory.Exists(ledgerDirectory)
            ? Directory.GetFiles(ledgerDirectory, "*" + LedgerSuffix, SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        return Check(il, files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))), handWrittenTypes);
    }

    /// <summary>
    /// Cross-checks <paramref name="il"/> against ledgers given as (name, JSON text). The sites of
    /// <paramref name="handWrittenTypes"/> (IL type names) are hand-written source no generator
    /// ledgers: they are listed apart instead of joined, and a listed type with no site is a problem,
    /// so the list cannot outlive the code it excuses.
    /// </summary>
    public static LedgerCrossCheckResult Check(
        AssemblyTransitionReport il, IEnumerable<(string Name, string Json)> ledgers, IReadOnlyCollection<string>? handWrittenTypes = null)
    {
        var problems = new List<string>();
        var issues = new List<string>();
        var entries = new List<LedgerEntryView>();
        var count = 0;
        foreach (var (name, json) in ledgers)
        {
            count++;
            ReadLedger(name, json, entries, issues, problems);
        }
        if (count == 0)
            problems.Add("no transition ledger was found; the generator writes one per module beside its C#");

        var handWritten = new HashSet<string>(handWrittenTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
        var allSites = IlItems(il).ToList();
        var sites = allSites.Where(s => !handWritten.Contains(TypeOf(s.Key))).ToList();
        var handWrittenSites = allSites.Where(s => handWritten.Contains(TypeOf(s.Key))).ToList();
        foreach (var type in handWritten.Where(t => handWrittenSites.All(s => TypeOf(s.Key) != t)).Order(StringComparer.Ordinal))
            problems.Add($"hand-written type {type} has no transition site; remove it from the hand-written list");

        // Bijection by kind and key, with multiplicity.
        var remaining = entries.GroupBy(e => (e.Kind, e.Key)).ToDictionary(g => g.Key, g => new Queue<LedgerEntryView>(g));
        var missingFromLedger = new List<string>();
        var mismatches = new List<string>();
        foreach (var site in sites)
        {
            if (!remaining.TryGetValue((site.Kind, site.Key), out var queue) || queue.Count == 0)
            {
                missingFromLedger.Add($"{site.Kind} {site.Key}");
                continue;
            }
            var entry = queue.Dequeue();
            if (site.Primitive is { } ilPrimitive && entry.Primitive != ilPrimitive)
                mismatches.Add($"{site.Kind} {site.Key}: the IL classifies it {(ilPrimitive ? "primitive" : "not primitive")}, the ledger {(entry.Primitive is { } p ? (p ? "primitive" : "not primitive") : "does not say")}");
        }
        var missingFromIl = remaining.Values.SelectMany(q => q).Select(e => $"{e.Kind} {e.Key} ({e.Origin})").ToList();

        return new LedgerCrossCheckResult(
            Tally(sites.Select(s => s.Kind)),
            Tally(entries.Select(e => e.Kind)),
            entries.Count(e => e.Primitive == true),
            Sorted(missingFromLedger),
            Sorted(missingFromIl),
            Sorted(mismatches),
            Sorted(entries.Where(e => e.Status == "Unplanned" && e.Kind != LedgerTransitionKind.CdeclSpecialPInvoke).Select(Describe)),
            Sorted(entries.Where(e => e.Status == "Unplanned" && e.Kind == LedgerTransitionKind.CdeclSpecialPInvoke).Select(Describe)),
            Sorted(entries.Where(e => e.Status == "Incomplete").Select(Describe)),
            Sorted(handWrittenSites.Select(s => $"{s.Kind} {s.Key}")),
            Sorted(issues),
            Sorted(problems));

        static string Describe(LedgerEntryView e) => $"{e.Kind} {e.Key}: {string.Join("; ", e.Reasons)}";
    }

    /// <summary>The IL type a join key's member belongs to.</summary>
    private static string TypeOf(string key)
    {
        var separator = key.IndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }

    /// <summary>The IL's transition sites, as the ledger names them.</summary>
    public static IEnumerable<LedgerJoinItem> IlItems(AssemblyTransitionReport il)
    {
        foreach (var site in il.Sites)
        {
            switch (site.Kind)
            {
                case TransitionSiteKind.PInvoke when site.Convention == TransitionConvention.Swift:
                    yield return new(LedgerTransitionKind.ForwardPInvoke, KeyOf(site.Member, site.EntryPoint ?? ""),
                        !site.Rules.Contains(TransitionRule.R1), il.AssemblyName);
                    break;
                case TransitionSiteKind.PInvoke when site.Rules.Contains(TransitionRule.R2):
                    yield return new(LedgerTransitionKind.CdeclSpecialPInvoke, KeyOf(site.Member, site.EntryPoint ?? ""), null, il.AssemblyName);
                    break;
                case TransitionSiteKind.UnmanagedCallersOnly when site.Convention == TransitionConvention.Swift:
                    yield return new(LedgerTransitionKind.ReverseCallback, site.Member, null, il.AssemblyName);
                    break;
                case TransitionSiteKind.CallbackAddress:
                    yield return new(LedgerTransitionKind.CallbackAddress, KeyOf(NormalizeIlMember(site.Member), site.Target ?? ""), null, il.AssemblyName);
                    break;
                case TransitionSiteKind.Calli when site.Convention == TransitionConvention.Swift:
                    yield return new(LedgerTransitionKind.ForwardCalli, NormalizeIlMember(site.Member),
                        !site.Rules.Contains(TransitionRule.R5), il.AssemblyName);
                    break;
            }
        }
    }

    public static string KeyOf(string member, string second) => string.Concat(member, " ", second);

    /// <summary>
    /// An IL member as the C# that produced it names it: a lambda's closure class
    /// (<c>+&lt;&gt;c__DisplayClassN_M</c>, <c>+&lt;&gt;c</c>) folded into the declaring type, and an async
    /// or iterator state machine's <c>+&lt;M&gt;d__N::MoveNext</c> into <c>::M</c>.
    /// </summary>
    public static string NormalizeIlMember(string member)
    {
        var separator = member.LastIndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return member;
        var type = member.Substring(0, separator);
        var method = member.Substring(separator + 2);
        var stateMachine = Regex.Match(type, @"\+<([^>]+)>d__\d+(?:`\d+)?$");
        if (stateMachine.Success && method == "MoveNext")
        {
            type = type.Substring(0, stateMachine.Index);
            method = stateMachine.Groups[1].Value;
        }
        type = Regex.Replace(type, @"\+<>c(?:__DisplayClass\w+)?(?:`\d+)?(?=\+|$)", "");
        return type + "::" + method;
    }

    private static void ReadLedger(string name, string json, List<LedgerEntryView> entries, List<string> issues, List<string> problems)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
            if (schema != LedgerSchema)
            {
                problems.Add($"{name}: ledger schema {schema}, but this cross-check reads schema {LedgerSchema}");
                return;
            }
            if (root.TryGetProperty("issues", out var issueArray))
                foreach (var issue in issueArray.EnumerateArray())
                    issues.Add($"{name}: {issue.GetString()}");
            var factIds = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("facts", out var factArray) && factArray.ValueKind == JsonValueKind.Array)
                foreach (var f in factArray.EnumerateArray())
                    if (Str(f, "id") is { Length: > 0 } id)
                        factIds.Add(id);
            foreach (var t in root.GetProperty("transitions").EnumerateArray())
            {
                var transition = t.TryGetProperty("transition", out var tr) ? tr.GetString() : null;
                if (!Enum.TryParse<LedgerTransitionKind>(transition, out var kind))
                {
                    problems.Add($"{name}: an entry has an unknown transition kind '{transition}'");
                    continue;
                }
                var member = t.GetProperty("member").GetString() ?? "";
                var key = kind switch
                {
                    LedgerTransitionKind.ForwardPInvoke or LedgerTransitionKind.CdeclSpecialPInvoke => KeyOf(member, Str(t, "entryPoint")),
                    LedgerTransitionKind.CallbackAddress => KeyOf(member, Str(t, "target")),
                    _ => member,
                };
                bool? primitive = t.TryGetProperty("carriers", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() == "Primitive"
                    : null;
                var reasons = t.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToImmutableArray()
                    : ImmutableArray<string>.Empty;
                var status = Str(t, "status");
                if (FactsProblem(kind, status, Str(t, "factsId"), t, factIds) is { } factsProblem)
                    issues.Add($"{name}: {kind} {key}: {factsProblem}");
                entries.Add(new LedgerEntryView(kind, key, primitive, status, reasons, name));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add($"{name}: the ledger does not read: {ex.Message}");
        }
    }

    /// <summary>
    /// Why an entry's status and its facts disagree, or null. A planned entry (complete or incomplete)
    /// cites a facts record the ledger holds, or, for a call several closures share, lists candidates it
    /// holds; an unplanned one is only allowed for a C-convention import, whose callee is not Swift.
    /// </summary>
    private static string? FactsProblem(LedgerTransitionKind kind, string status, string factsId, JsonElement entry, HashSet<string> factIds)
    {
        var candidates = entry.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        switch (status)
        {
            case "Complete" or "Incomplete" when factsId.Length > 0:
                return factIds.Contains(factsId) ? null : $"cites facts '{factsId}' the ledger does not hold";
            case "Incomplete" when candidates.Count > 1:
                var missing = candidates.Where(id => !factIds.Contains(id)).ToList();
                return missing.Count == 0 ? null : $"lists candidate facts the ledger does not hold: {string.Join(", ", missing)}";
            case "Complete" or "Incomplete":
                return $"is {status.ToLowerInvariant()} but cites no facts";
            case "Unplanned":
                return null;
            default:
                return $"has an unknown status '{status}'";
        }
    }

    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static ImmutableSortedDictionary<LedgerTransitionKind, int> Tally(IEnumerable<LedgerTransitionKind> kinds) =>
        kinds.GroupBy(k => k).ToImmutableSortedDictionary(g => g.Key, g => g.Count());

    private static ImmutableArray<string> Sorted(IEnumerable<string> lines) => lines.Order(StringComparer.Ordinal).ToImmutableArray();

    /// <summary>A readable account of the result, every failing line included.</summary>
    public static string ToSummary(LedgerCrossCheckResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Transition ledger cross-check: {(result.Passed ? "PASSED" : "FAILED")}");
        foreach (var kind in Enum.GetValues<LedgerTransitionKind>())
        {
            result.IlSites.TryGetValue(kind, out var sites);
            result.LedgerEntries.TryGetValue(kind, out var ledger);
            sb.AppendLine($"  {kind,-20} IL {sites,6}   ledger {ledger,6}");
        }
        sb.AppendLine($"  primitive-marked entries: {result.PrimitiveMarked}");
        Section(sb, "IL sites with no ledger entry", result.MissingFromLedger);
        Section(sb, "ledger entries with no IL site", result.MissingFromIl);
        Section(sb, "carrier verdicts that disagree", result.CarrierMismatches);
        Section(sb, "unplanned entries", result.Unplanned);
        Section(sb, "ledger issues", result.LedgerIssues);
        Section(sb, "problems", result.Problems);
        Section(sb, "C-convention register-type imports with no Swift facts (reported, not failed)", result.UnplannedCdeclSpecial);
        Section(sb, "incomplete entries (reported, not failed)", result.Incomplete);
        Section(sb, "hand-written transitions, not ledgered (reported, not failed)", result.HandWritten);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, ImmutableArray<string> lines)
    {
        if (lines.IsEmpty)
            return;
        sb.AppendLine($"{title} ({lines.Length}):");
        foreach (var line in lines)
            sb.AppendLine("  " + line);
    }
}
