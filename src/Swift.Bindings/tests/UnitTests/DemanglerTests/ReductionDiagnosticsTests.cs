// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 18 — unit coverage for the <see cref="ReductionDiagnostics.Snapshot"/> formatting and the
/// allowlist gating that decides whether SWIFTBIND058 fires. Snapshots are constructed directly (not
/// via the process-global accumulator), so these are hermetic and race-free under xUnit's parallel
/// class execution.
/// </summary>
public class ReductionDiagnosticsTests
{
    private static ReductionDiagnostics.Snapshot Make(
        long attempts,
        params (NodeKind kind, int count, string example)[] misses)
    {
        var byKind = misses.ToDictionary(m => m.kind, m => m.count);
        var examples = misses.ToDictionary(m => m.kind, m => m.example);
        var total = misses.Sum(m => (long)m.count);
        return new ReductionDiagnostics.Snapshot(attempts, total, byKind, examples);
    }

    [Fact]
    public void Describe_OrdersKindsByNameAndIncludesCountAndExample()
    {
        var snapshot = Make(100,
            (NodeKind.Tuple, 2, "$sTupleExample"),
            (NodeKind.Constructor, 3, "$sCtorExample"));

        // Ordered by kind name (Constructor before Tuple), each "Kind xN (e.g. symbol)".
        Assert.Equal(
            "Constructor x3 (e.g. $sCtorExample); Tuple x2 (e.g. $sTupleExample)",
            snapshot.Describe());
        Assert.True(snapshot.HasMisses);
    }

    [Fact]
    public void Describe_NoMisses_IsEmpty()
    {
        var snapshot = Make(50);
        Assert.False(snapshot.HasMisses);
        Assert.Equal(string.Empty, snapshot.Describe());
        Assert.Equal(string.Empty, snapshot.DescribeUnexpected());
        Assert.False(snapshot.HasUnexpectedMisses);
    }

    [Fact]
    public void AllowlistedMissesOnly_DoNotCountAsUnexpected()
    {
        // Constructor and Getter are both on the IntentionallyUnreducedKinds allowlist, so a run that
        // only misses those (the common case for every real library) must NOT trip SWIFTBIND058.
        var snapshot = Make(200,
            (NodeKind.Constructor, 5, "$sCtor"),
            (NodeKind.Getter, 7, "$sGetter"));

        Assert.True(snapshot.HasMisses);
        Assert.False(snapshot.HasUnexpectedMisses);
        Assert.Equal(string.Empty, snapshot.DescribeUnexpected());
    }

    [Fact]
    public void UnexpectedMiss_IsIsolatedFromAllowlistedMisses()
    {
        // A genuinely new hole (Tuple is not allowlisted) must surface — and only it, not the benign
        // Constructor miss alongside it.
        var snapshot = Make(200,
            (NodeKind.Constructor, 5, "$sCtor"),
            (NodeKind.Tuple, 1, "$sTupleHole"));

        Assert.True(snapshot.HasUnexpectedMisses);
        Assert.Equal("Tuple x1 (e.g. $sTupleHole)", snapshot.DescribeUnexpected());
        // Describe() still reports everything for full context.
        Assert.Contains("Constructor x5", snapshot.Describe());
        Assert.Contains("Tuple x1", snapshot.Describe());
    }

    [Fact]
    public void IntentionallyUnreducedKinds_AllHaveNonEmptyReasons()
    {
        Assert.NotEmpty(ReductionDiagnostics.IntentionallyUnreducedKinds);
        foreach (var (kind, reason) in ReductionDiagnostics.IntentionallyUnreducedKinds)
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{kind} allowlist entry needs a reason.");
    }
}
