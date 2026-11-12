// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration;

/// <summary>
/// What swiftc gave each bridge target, per toolchain and per target triple: the calling convention
/// and normalized physical signature of the function swiftc defined (or, for a symbol another module
/// owns, declared when calling it). A plan's prediction is a model; this is the compiler's answer,
/// and only a plan whose target agrees with it on every triple under every recorded toolchain is
/// executable.
/// </summary>
/// <remarks>
/// The <c>swift-lowering-oracle</c> target writes it, one toolchain section per run; each section
/// replaces the one of the same toolchain and keeps the others.
/// </remarks>
public sealed record SwiftLoweringEvidence
{
    public const int CurrentSchema = 1;

    /// <summary>The triples every target is compiled for: the device and both simulators.</summary>
    public static readonly ImmutableArray<string> RequiredTriples =
        ["arm64-apple-ios15.0", "arm64-apple-ios15.0-simulator", "x86_64-apple-ios15.0-simulator"];

    public required int Schema { get; init; }

    /// <summary>The triples the file was recorded for, which must be <see cref="RequiredTriples"/>.</summary>
    public required ImmutableArray<string> Triples { get; init; }

    public required ImmutableArray<SwiftLoweringEvidenceToolchain> Toolchains { get; init; }

    public static SwiftLoweringEvidence Empty => new() { Schema = CurrentSchema, Triples = RequiredTriples, Toolchains = [] };

    public static SwiftLoweringEvidence Load(string path)
    {
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(path), SwiftLoweringEvidenceJsonContext.Default.SwiftLoweringEvidence)
            ?? throw new InvalidDataException($"{path} holds no lowering evidence");
        if (evidence.Schema != CurrentSchema)
            throw new InvalidDataException($"{path} has schema {evidence.Schema}; this generator reads {CurrentSchema}");
        if (evidence.Toolchains.IsDefault)
            throw new InvalidDataException($"{path} names no toolchains");
        // The required triples are this generator's, not the file's: evidence that names fewer
        // would let a plan through on a triple swiftc was never asked about.
        if (evidence.Triples.IsDefault || !evidence.Triples.SequenceEqual(RequiredTriples, StringComparer.Ordinal))
            throw new InvalidDataException($"{path} names the triples [{(evidence.Triples.IsDefault ? "" : string.Join(", ", evidence.Triples))}]; this generator requires [{string.Join(", ", RequiredTriples)}]");
        return evidence;
    }

    /// <summary>The evidence as JSON, every map in ordinal order whatever comparer it was read with.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with
    {
        Toolchains = Toolchains.OrderBy(t => t.Toolchain, StringComparer.Ordinal)
            .Select(t => t with
            {
                Triples = t.Triples.ToImmutableSortedDictionary(p => p.Key, p => p.Value.ToImmutableSortedDictionary(StringComparer.Ordinal), StringComparer.Ordinal),
            })
            .ToImmutableArray(),
    }, SwiftLoweringEvidenceJsonContext.Default.SwiftLoweringEvidence) + "\n";

    /// <summary>This evidence with one toolchain's signatures on one triple replaced.</summary>
    public SwiftLoweringEvidence WithTriple(string toolchain, string triple, IReadOnlyDictionary<string, string> symbols)
    {
        var existing = Toolchains.FirstOrDefault(t => t.Toolchain == toolchain);
        var triples = (existing?.Triples ?? ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, string>>.Empty.WithComparers(StringComparer.Ordinal))
            .SetItem(triple, symbols.ToImmutableSortedDictionary(StringComparer.Ordinal));
        var toolchains = Toolchains.Where(t => t.Toolchain != toolchain)
            .Append(new SwiftLoweringEvidenceToolchain(toolchain, triples))
            .OrderBy(t => t.Toolchain, StringComparer.Ordinal)
            .ToImmutableArray();
        return this with { Toolchains = toolchains };
    }

    /// <summary>
    /// Whether a forward plan of <paramref name="symbol"/> predicting <paramref name="callee"/> is
    /// executable, and why: every toolchain must show <c>swiftcc</c> and exactly that signature on
    /// every triple.
    /// </summary>
    public (bool Executable, string Evidence) Judge(string symbol, string callee)
    {
        if (Toolchains.IsDefaultOrEmpty)
            return (false, SwiftCallPlanSet.NoEvidenceReason);
        var expected = "swiftcc " + callee;
        var failures = new List<string>();
        foreach (var toolchain in Toolchains)
        {
            foreach (var triple in RequiredTriples)
            {
                if (!toolchain.Triples.TryGetValue(triple, out var symbols))
                    failures.Add($"no evidence for {triple} under {toolchain.Toolchain}");
                else if (!symbols.TryGetValue(symbol, out var actual))
                    failures.Add($"swiftc shows no {symbol} on {triple} under {toolchain.Toolchain}");
                else if (actual != expected)
                    failures.Add($"swiftc gives {actual} on {triple} under {toolchain.Toolchain}; the plan predicts {expected}");
            }
        }
        return failures.Count == 0
            ? (true, $"swiftc gives {expected} on {RequiredTriples.Length} triple(s) under {Toolchains.Length} toolchain(s)")
            : (false, string.Join("; ", failures));
    }

    /// <summary>The reason a closure's plan is never executable on evidence: swiftc defines no function under a closure's pointer.</summary>
    public const string ClosureReason = "a closure invocation has no swiftc definition to compare: its plan has no evidence";

    /// <summary>
    /// <paramref name="coverage"/> with each planned entry's executable flag and evidence set. A
    /// forward entry is judged by its target and predicted callee; a thick or reverse one is not
    /// executable; any unread argument without a reason fails closed.
    /// </summary>
    public ImmutableArray<SwiftPlanCoverageEntry> Join(ImmutableArray<SwiftPlanCoverageEntry> coverage, ImmutableArray<SwiftCallPlan> plans)
    {
        var bySymbol = plans.ToDictionary(p => p.Symbol, StringComparer.Ordinal);
        return coverage.Select(entry =>
        {
            if (entry.Status != SwiftPlanStatus.Planned)
                return entry with { Executable = false, Evidence = null };
            if (entry.Symbol is not { } symbol || !bySymbol.TryGetValue(symbol, out var plan))
                return entry with { Executable = false, Evidence = $"its plan {entry.Symbol} is not in the set" };
            if (plan.Body.Kind != SwiftPlanKind.Forward || plan.Body.Target is null)
                return entry with { Executable = false, Evidence = ClosureReason };
            if (plan.Body.Managed.Arguments.Any(a => a.Unread && a.UnreadReason is null))
                return entry with { Executable = false, Evidence = "an argument the callee does not read has no reason it is benign" };
            if (entry.Callee is not { } callee)
                return entry with { Executable = false, Evidence = "the entry records no predicted callee signature" };
            var (executable, evidence) = Judge(plan.Body.Target, callee);
            return entry with { Executable = executable, Evidence = evidence };
        }).ToImmutableArray();
    }
}

/// <summary>One toolchain's evidence: per triple, each symbol's <c>convention signature</c>.</summary>
/// <param name="Toolchain"><c>swiftc --version</c>'s first line.</param>
public sealed record SwiftLoweringEvidenceToolchain(
    string Toolchain,
    ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, string>> Triples);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SwiftLoweringEvidence))]
internal partial class SwiftLoweringEvidenceJsonContext : JsonSerializerContext
{
}
