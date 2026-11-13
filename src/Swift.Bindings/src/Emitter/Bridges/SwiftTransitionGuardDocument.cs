// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>One transition a guard names, in the sidecar.</summary>
/// <param name="Name">Its constant in the module's guard class.</param>
/// <param name="Kind">The transition kind's name.</param>
/// <param name="Member">The IL member that holds it.</param>
/// <param name="EntryPoint">An import's symbol, or a call's pointer type and ordinal.</param>
/// <param name="Facts">Its shapes as the guard class holds them: none once it is routed through a bridge.</param>
/// <param name="Shapes">The names of <paramref name="Facts"/>.</param>
/// <param name="Residual">Why no bridge carries it, or null once one does.</param>
/// <param name="Pending">Whether the wrapper compile may still route it: it has a bridge plan no swiftc evidence has judged yet.</param>
internal sealed record SwiftTransitionGuardTransition(
    string Name, string Kind, string Member, string EntryPoint, ulong Facts, string Shapes, string? Residual, bool Pending);

/// <summary>
/// The guarded members of a module and the transitions they reach, beside its C# as
/// <c>{namespace}.swift-transition-guards.json</c>: generation writes it, and the wrapper compile's
/// bridge routing rewrites it with the shapes its routing leaves. The binding report reads its
/// members from it, checked against the guards the final C# holds.
/// </summary>
internal sealed record SwiftTransitionGuardDocument
{
    public const int SchemaVersion = 1;

    public required int Schema { get; init; }

    public required string Module { get; init; }

    public required string Namespace { get; init; }

    public required ImmutableArray<SwiftTransitionGuardTransition> Transitions { get; init; }

    public required ImmutableArray<SwiftTransitionGuardSite> Sites { get; init; }

    public static string FileName(string @namespace) => SwiftTransitionGuards.SidecarFileName(@namespace);

    public const string FilePattern = "*.swift-transition-guards.json";

    public static SwiftTransitionGuardDocument Parse(string json, string source)
    {
        var document = JsonSerializer.Deserialize(json, SwiftTransitionGuardJsonContext.Default.SwiftTransitionGuardDocument)
            ?? throw new InvalidDataException($"{source} holds no transition guard document");
        if (document.Schema != SchemaVersion)
            throw new InvalidDataException($"{source} has transition guard schema {document.Schema}; this generator reads {SchemaVersion}");
        return document;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SwiftTransitionGuardJsonContext.Default.SwiftTransitionGuardDocument) + "\n";

    /// <summary>The shapes a site's guard refuses on: those of every transition it names.</summary>
    public SwiftTransitionFacts FactsOf(SwiftTransitionGuardSite site)
    {
        var facts = Transitions.ToDictionary(t => t.Name, t => (SwiftTransitionFacts)t.Facts, StringComparer.Ordinal);
        return site.Transitions.Aggregate(SwiftTransitionFacts.None, (all, name) => all | facts.GetValueOrDefault(name));
    }

    /// <summary>The members code outside the binding can call that refuse on some runtime.</summary>
    public IEnumerable<SwiftTransitionGuardSite> GuardedMembers()
    {
        var facts = Transitions.ToDictionary(t => t.Name, t => (SwiftTransitionFacts)t.Facts, StringComparer.Ordinal);
        return Sites.Where(s => s.Visible && s.Transitions.Any(t => facts.GetValueOrDefault(t) != SwiftTransitionFacts.None));
    }

    /// <summary>
    /// The document with each transition's shapes taken from <paramref name="masks"/>, the guard class
    /// the C# now compiles with, and its residual reason from <paramref name="plans"/>, as judged and
    /// routed now. Nothing is pending any more.
    /// </summary>
    public SwiftTransitionGuardDocument Settle(IReadOnlyList<SwiftTransitionGuards.Mask> masks, SwiftCallPlanSet plans)
    {
        var byName = masks.ToDictionary(m => m.Name, m => m.Facts, StringComparer.Ordinal);
        var coverage = SwiftTransitionGuards.CoverageOf(plans);
        return this with
        {
            Transitions = Transitions.Select(t =>
            {
                var facts = byName.GetValueOrDefault(t.Name);
                return t with
                {
                    Facts = (ulong)facts,
                    Shapes = facts.ToString(),
                    Residual = facts == SwiftTransitionFacts.None ? null : SwiftTransitionGuards.ResidualReason(t.Kind, t.Member, t.EntryPoint, coverage),
                    Pending = false,
                };
            }).ToImmutableArray(),
        };
    }

    /// <summary>
    /// Logs each guarded member (<see cref="SwiftTransitionGuards.MemberCode"/>) and the summary
    /// (<see cref="SwiftTransitionGuards.SummaryCode"/>). A member every transition of which the wrapper
    /// compile may still route is logged as information; the rest, and the summary, as warnings.
    /// </summary>
    public void Log(ILogger logger)
    {
        var transitions = Transitions.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var members = GuardedMembers().ToList();
        if (members.Count == 0)
            return;
        var pending = 0;
        foreach (var site in members)
        {
            var reached = site.Transitions.Select(n => transitions.GetValueOrDefault(n)).Where(t => t is not null && t.Facts != 0).Cast<SwiftTransitionGuardTransition>().ToList();
            var facts = reached.Aggregate(SwiftTransitionFacts.None, (all, t) => all | (SwiftTransitionFacts)t.Facts);
            var why = string.Join("; ", reached.Select(t => t.Residual).Where(r => r is not null).Distinct(StringComparer.Ordinal));
            if (reached.All(t => t.Pending))
            {
                pending++;
                logger.LogInformation(
                    "{Code}: {Member} reaches a direct Swift-convention transition ({Facts}) that the wrapper compile may yet route through a native bridge; until then it refuses with SwiftTransitionUnsupportedException on runtimes that do not execute that shape correctly.",
                    SwiftTransitionGuards.MemberCode, site.Display, facts);
            }
            else
            {
                logger.LogWarning(
                    "{Code}: {Member} refuses with SwiftTransitionUnsupportedException, before any native work, on runtimes that do not execute a direct Swift-convention transition of shape {Facts} correctly (Mono, and the CoreCLR interpreter); it works on NativeAOT and CoreCLR with its JIT. It has no native bridge: {Reason}",
                    SwiftTransitionGuards.MemberCode, site.Display, facts, why);
            }
        }
        var byShape = members
            .Select(FactsOf)
            .GroupBy(f => f)
            .OrderBy(g => (ulong)g.Key)
            .Select(g => $"{g.Count()} {g.Key}");
        logger.LogWarning(
            "{Code}: {Module}: {Count} member(s) reach a direct Swift-convention transition and refuse, before any native work, on runtimes that do not execute its shape correctly ({Shapes}){Pending}. The binding report lists each and why it has no native bridge.",
            SwiftTransitionGuards.SummaryCode, Module, members.Count, string.Join(", ", byShape),
            pending > 0 ? $"; the wrapper compile may yet route {pending} of them through native bridges" : "");
    }
}

/// <summary>Source-generated, trim-safe serialization of the transition guard sidecar.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SwiftTransitionGuardDocument))]
internal partial class SwiftTransitionGuardJsonContext : JsonSerializerContext
{
}
