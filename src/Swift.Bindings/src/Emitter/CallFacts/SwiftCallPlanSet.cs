// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>What the plan set says about one ledger transition.</summary>
public enum SwiftPlanStatus
{
    /// <summary>It has a plan; <see cref="SwiftPlanCoverageEntry.Symbol"/> names its bridge.</summary>
    Planned,

    /// <summary>It needs a bridge and the planner refused it; the refusals say why.</summary>
    Refused,

    /// <summary>It needs no bridge; the reason says why.</summary>
    NotBridged,
}

/// <summary>
/// One ledger transition's planning: its plan's bridge, or the refusals, or why it needs no bridge.
/// Keyed like the ledger entry it covers.
/// </summary>
public sealed record SwiftPlanCoverageEntry(
    SwiftTransitionKind Transition,
    string Member,
    string EntryPoint,
    SwiftPlanStatus Status)
{
    /// <summary>For a callback address: the callback whose plan it uses.</summary>
    public string? Target { get; init; }

    public string? FactsId { get; init; }

    /// <summary>The bridge symbol, when planned.</summary>
    public string? Symbol { get; init; }

    /// <summary>The plan hash, when planned.</summary>
    public string? Hash { get; init; }

    /// <summary>The conventions the plan was made under, from the facts, when planned.</summary>
    public SwiftPlanSemantics? Semantics { get; init; }

    /// <summary>Why it needs no bridge.</summary>
    public string? Reason { get; init; }

    public ImmutableArray<SwiftPlanRefusal> Refusals { get; init; } = ImmutableArray<SwiftPlanRefusal>.Empty;
}

/// <summary>
/// The plans of one module: one coverage entry per ledger transition, and the distinct plans they
/// use. Every transition that needs a bridge is planned or refused with a reason; identical plans
/// share one entry, named by their hash.
/// </summary>
/// <remarks>
/// <para>
/// A forward P/Invoke or <c>calli</c> whose managed signature carries only primitives (design
/// invariant I1) is placed identically by every runtime and needs no bridge; so does a C-convention
/// import, whose Swift register types are normalized away rather than bridged. Every other forward
/// transition and every Swift-convention callback is planned. A callback address uses its callback's
/// plan.
/// </para>
/// <para>
/// The plan set is deterministic: the coverage follows the ledger's order and the plans are sorted
/// by symbol, so two runs over the same input write the same bytes. Writing it changes nothing that
/// is emitted.
/// </para>
/// </remarks>
public sealed class SwiftCallPlanSet
{
    public const string PrimitiveOnlyReason = "primitive-only: every runtime places it identically (I1)";

    public const string CdeclSpecialReason = "a C-convention import that names a Swift register type: normalized, not bridged";

    public required string Module { get; init; }

    public required ImmutableArray<SwiftPlanCoverageEntry> Coverage { get; init; }

    /// <summary>The distinct plans, by symbol.</summary>
    public required ImmutableArray<SwiftCallPlan> Plans { get; init; }

    public int Count(SwiftPlanStatus status) => Coverage.Count(e => e.Status == status);

    /// <summary>Plans every transition of <paramref name="ledger"/> with <paramref name="planner"/>.</summary>
    public static SwiftCallPlanSet Build(SwiftTransitionLedger ledger, SwiftCallPlanner planner)
    {
        var facts = ledger.Facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        SwiftCallFacts? FactsOf(SwiftTransitionLedgerEntry e) => e.FactsId is { } id && facts.TryGetValue(id, out var f) ? f : null;

        var outcomes = new List<(SwiftTransitionLedgerEntry Entry, SwiftPlanOutcome? Outcome, string? Reason)>();
        foreach (var entry in ledger.Entries)
        {
            switch (entry.Transition)
            {
                case SwiftTransitionKind.CdeclSpecialPInvoke:
                    outcomes.Add((entry, null, CdeclSpecialReason));
                    break;
                case SwiftTransitionKind.ForwardPInvoke or SwiftTransitionKind.ForwardCalli when entry.Carriers == SwiftTransitionCarriers.Primitive:
                    outcomes.Add((entry, null, PrimitiveOnlyReason));
                    break;
                case SwiftTransitionKind.CallbackAddress:
                    outcomes.Add((entry, null, null));
                    break;
                default:
                    outcomes.Add((entry, Plan(entry, FactsOf(entry), planner), null));
                    break;
            }
        }
        return Assemble(ledger.Module, outcomes);
    }

    /// <summary>Plans one transition the ledger lists.</summary>
    public static SwiftPlanOutcome Plan(SwiftTransitionLedgerEntry entry, SwiftCallFacts? facts, SwiftCallPlanner planner)
    {
        if (entry.Signature is not { } signature)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, "the transition's managed signature was not read"));
        if (facts is null)
        {
            var why = entry.Candidates is { } candidates
                ? $"{candidates.Length} closures of different Swift types share its pointer type"
                : entry.Reasons.IsDefaultOrEmpty ? "the transition has no facts" : string.Join("; ", entry.Reasons);
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, why));
        }
        // The ledger's reasons cover more than the facts' own unknowns (two producers disagreeing).
        if (entry.Status != SwiftTransitionLedgerStatus.Complete)
            return SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, string.Join("; ", entry.Reasons)));
        return entry.Transition switch
        {
            SwiftTransitionKind.ForwardPInvoke => planner.PlanForward(facts, signature, entry.EntryPoint),
            SwiftTransitionKind.ForwardCalli => planner.PlanForward(facts, signature, null),
            SwiftTransitionKind.ReverseCallback => planner.PlanReverse(facts, signature),
            _ => SwiftPlanOutcome.Refused(new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, $"a {entry.Transition} is not planned on its own")),
        };
    }

    private static SwiftPlanCoverageEntry Covered(SwiftTransitionLedgerEntry entry, SwiftPlanStatus status) =>
        new(entry.Transition, entry.Member, entry.EntryPoint, status) { Target = entry.Target, FactsId = entry.FactsId };

    internal static SwiftCallPlanSet Assemble(
        string module, List<(SwiftTransitionLedgerEntry Entry, SwiftPlanOutcome? Outcome, string? Reason)> outcomes)
    {
        // A bridge is named by a prefix of its hash: two different plans whose prefixes collide
        // cannot both be emitted under one name, so every transition using either is refused.
        var collisions = outcomes.Where(o => o.Outcome?.Plan is not null)
            .Select(o => o.Outcome!.Plan!)
            .GroupBy(p => p.Symbol, StringComparer.Ordinal)
            .Where(g => g.Select(p => p.Hash).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var plans = new SortedDictionary<string, SwiftCallPlan>(StringComparer.Ordinal);
        var coverage = new List<SwiftPlanCoverageEntry>();
        var byCallback = new Dictionary<string, SwiftPlanCoverageEntry>(StringComparer.Ordinal);
        foreach (var (entry, outcome, reason) in outcomes)
        {
            if (outcome is null)
                continue;
            SwiftPlanCoverageEntry covered;
            if (outcome.Plan is { } plan && collisions.Contains(plan.Symbol))
            {
                covered = Covered(entry, SwiftPlanStatus.Refused) with
                {
                    Refusals = [new SwiftPlanRefusal(SwiftPlanRefusalKind.HashCollision, $"two different plans share the bridge name {plan.Symbol}")],
                };
            }
            else if (outcome.Plan is { } planned)
            {
                plans.TryAdd(planned.Symbol, planned);
                covered = Covered(entry, SwiftPlanStatus.Planned) with { Symbol = planned.Symbol, Hash = planned.Hash, Semantics = outcome.Semantics };
            }
            else
            {
                covered = Covered(entry, SwiftPlanStatus.Refused) with { Refusals = outcome.Refusals };
            }
            if (entry.Transition == SwiftTransitionKind.ReverseCallback)
                byCallback.TryAdd(entry.Member, covered);
            coverage.Add(covered);
        }

        foreach (var (entry, outcome, reason) in outcomes)
        {
            if (outcome is not null)
                continue;
            if (reason is not null)
            {
                coverage.Add(Covered(entry, SwiftPlanStatus.NotBridged) with { Reason = reason });
                continue;
            }
            // A callback address uses its callback's plan.
            if (entry.Target is { } target && byCallback.TryGetValue(target, out var callback))
            {
                coverage.Add(Covered(entry, callback.Status) with
                {
                    Symbol = callback.Symbol, Hash = callback.Hash, Semantics = callback.Semantics, Refusals = callback.Refusals,
                });
            }
            else
            {
                coverage.Add(Covered(entry, SwiftPlanStatus.Refused) with
                {
                    Refusals = [new SwiftPlanRefusal(SwiftPlanRefusalKind.Facts, $"no callback '{entry.Target}' is listed for this address")],
                });
            }
        }

        return new SwiftCallPlanSet
        {
            Module = module,
            Coverage = coverage
                .OrderBy(e => e.Transition)
                .ThenBy(e => e.Member, StringComparer.Ordinal)
                .ThenBy(e => e.EntryPoint, StringComparer.Ordinal)
                .ThenBy(e => e.Target, StringComparer.Ordinal)
                .ToImmutableArray(),
            Plans = plans.Values.ToImmutableArray(),
        };
    }

    // ---- the sidecar ----

    public static string FileName(string @namespace) => $"{@namespace}.swift-call-plans.json";

    public SwiftCallPlanDocument ToDocument() => new()
    {
        PlanVersion = SwiftCallPlan.Version,
        FactsVersion = SwiftCallFacts.CurrentVersion,
        Module = Module,
        Totals = new SwiftCallPlanTotals
        {
            Transitions = Coverage.Length,
            Planned = Count(SwiftPlanStatus.Planned),
            Refused = Count(SwiftPlanStatus.Refused),
            NotBridged = Count(SwiftPlanStatus.NotBridged),
            Plans = Plans.Length,
            ByTransition = Coverage.GroupBy(e => $"{e.Transition}/{e.Status}")
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ByPlanKind = Plans.GroupBy(p => p.Body.Kind.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ByRefusal = Coverage.SelectMany(e => e.Refusals.Select(r => r.Kind).Distinct())
                .GroupBy(k => k.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        },
        Coverage = Coverage,
        Plans = Plans,
    };

    public string ToJson() =>
        JsonSerializer.Serialize(ToDocument(), SwiftCallPlanJsonContext.Default.SwiftCallPlanDocument) + "\n";

    /// <summary>
    /// Writes the plan set next to the module's C#. A diagnostic artifact, like the ledger: a failure
    /// to write it is logged and never fails generation.
    /// </summary>
    public void Write(string outputDirectory, string @namespace, ILogger logger)
    {
        try
        {
            File.WriteAllText(Path.Combine(outputDirectory, FileName(@namespace)), ToJson());
            var refused = Count(SwiftPlanStatus.Refused);
            if (refused > 0)
                logger.LogInformation(
                    "Swift call plans for {Module}: {Refused} of {Total} transitions refused a bridge plan",
                    Module, refused, Coverage.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            logger.LogWarning("Could not write the Swift call plans for {Module}: {Message}", Module, ex.Message);
        }
    }
}

/// <summary>The serialized form of <see cref="SwiftCallPlanSet"/>.</summary>
public sealed record SwiftCallPlanDocument
{
    public required int PlanVersion { get; init; }
    public required int FactsVersion { get; init; }
    public required string Module { get; init; }
    public required SwiftCallPlanTotals Totals { get; init; }
    public required ImmutableArray<SwiftPlanCoverageEntry> Coverage { get; init; }
    public required ImmutableArray<SwiftCallPlan> Plans { get; init; }
}

/// <summary>The plan set's counts.</summary>
public sealed record SwiftCallPlanTotals
{
    public required int Transitions { get; init; }
    public required int Planned { get; init; }
    public required int Refused { get; init; }
    public required int NotBridged { get; init; }

    /// <summary>Distinct plans: one per bridge.</summary>
    public required int Plans { get; init; }

    /// <summary>Coverage entries per transition kind and status (<c>ForwardPInvoke/Planned</c>).</summary>
    public required ImmutableSortedDictionary<string, int> ByTransition { get; init; }

    public required ImmutableSortedDictionary<string, int> ByPlanKind { get; init; }

    /// <summary>Refused entries per refusal kind (an entry counts once per kind).</summary>
    public required ImmutableSortedDictionary<string, int> ByRefusal { get; init; }
}

/// <summary>Source-generated, trim-safe serialization of the plan set.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SwiftCallPlanDocument))]
internal partial class SwiftCallPlanJsonContext : JsonSerializerContext
{
}

/// <summary>The canonical form a plan's hash is computed over: compact, in declaration order, enums by name.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SwiftCallPlanBody))]
internal partial class SwiftCallPlanCanonicalJsonContext : JsonSerializerContext
{
}
