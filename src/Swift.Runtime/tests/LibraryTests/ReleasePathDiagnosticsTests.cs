// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies the aggregate release-path counter wiring: every increment method advances its OWN
/// field, and <see cref="ReleasePathDiagnostics.Reset"/> zeroes the counters. A miswired increment
/// (targeting the wrong field) would misattribute a release to the wrong end-state in a leak-probe
/// readout, so the wiring is what these tests pin down.
///
/// The counters are process-global and other tests in this assembly touch real value-witness
/// dispose/finalize paths concurrently, so the assertions are delta-based: increments from parallel
/// activity only ever RAISE a counter, so a method that advances its own field raises that field's
/// reading by at least the call count regardless of noise. Only <see cref="ReleasePathDiagnostics.Reset"/>
/// can LOWER a counter, and it is never called concurrently (the sole callers are this collection's
/// serialized tests and the RuntimeTestsApp harness, which is a different process) — so a reading
/// that dropped proves the reset ran.
/// </summary>
[Collection("ReleasePathDiagnostics")]
public class ReleasePathDiagnosticsTests
{
    // Pull one counter's numeric reading out of the single-line snapshot, scoped to its group so
    // the doubly-used field names (vwtDestroyInvoked, releaseCatch appear under both finalizer and
    // dispose) resolve unambiguously.
    private static long Field(string snapshot, string group, string field)
    {
        int gi = snapshot.IndexOf(group + "(", StringComparison.Ordinal);
        Assert.True(gi >= 0, $"group '{group}' missing from snapshot: {snapshot}");
        int inner = gi + group.Length + 1;
        int close = snapshot.IndexOf(')', inner);
        string body = snapshot.Substring(inner, close - inner);

        int fi = body.IndexOf(field + "=", StringComparison.Ordinal);
        Assert.True(fi >= 0, $"field '{field}' missing from group '{group}': {body}");
        int vs = fi + field.Length + 1;
        int ve = vs;
        while (ve < body.Length && char.IsDigit(body[ve]))
            ve++;
        return long.Parse(body.Substring(vs, ve - vs));
    }

    [Theory]
    // group, field, and the increment method exercised — one row per counter.
    [InlineData("wireDestroy", "entered", 0)]
    [InlineData("wireDestroy", "completed", 1)]
    [InlineData("wireDestroy", "metadataUnavailable", 2)]
    [InlineData("wireDestroy", "skippedInvalid", 3)]
    [InlineData("finalizer", "vwtDestroyInvoked", 4)]
    [InlineData("finalizer", "metadataZeroSkip", 5)]
    [InlineData("finalizer", "releaseCatch", 6)]
    [InlineData("dispose", "vwtDestroyInvoked", 7)]
    [InlineData("dispose", "releaseCatch", 8)]
    [InlineData("dispose", "metadataInvalidSkip", 9)]
    public void EachIncrement_AdvancesItsOwnField(string group, string field, int which)
    {
        long before = Field(ReleasePathDiagnostics.Snapshot(), group, field);
        Increment(which);
        Increment(which);
        long after = Field(ReleasePathDiagnostics.Snapshot(), group, field);

        // Two calls raise this field by AT LEAST two; concurrent activity can only add more. If the
        // method were wired to a different field, this field's reading would not move for our calls.
        Assert.True(after - before >= 2,
            $"{group}.{field} advanced by {after - before} (before={before}, after={after})");
    }

    [Fact]
    public void Reset_LowersAnInflatedCounter()
    {
        for (int i = 0; i < 1000; i++)
            ReleasePathDiagnostics.OnWireDestroyEntered();

        long inflated = Field(ReleasePathDiagnostics.Snapshot(), "wireDestroy", "entered");
        Assert.True(inflated >= 1000, $"expected >= 1000 after inflation, got {inflated}");

        ReleasePathDiagnostics.Reset();
        long afterReset = Field(ReleasePathDiagnostics.Snapshot(), "wireDestroy", "entered");

        // Only Reset can lower a counter, and nothing resets concurrently, so a drop below the
        // inflated reading proves the reset zeroed it (small concurrent increments cannot approach
        // the 1000 we added).
        Assert.True(afterReset < inflated,
            $"reset did not lower the counter: inflated={inflated}, afterReset={afterReset}");
    }

    private static void Increment(int which)
    {
        switch (which)
        {
            case 0: ReleasePathDiagnostics.OnWireDestroyEntered(); break;
            case 1: ReleasePathDiagnostics.OnWireDestroyCompleted(); break;
            case 2: ReleasePathDiagnostics.OnWireDestroyMetadataUnavailable(); break;
            case 3: ReleasePathDiagnostics.OnWireDestroySkippedInvalid(); break;
            case 4: ReleasePathDiagnostics.OnFinalizerVwtDestroyInvoked(); break;
            case 5: ReleasePathDiagnostics.OnFinalizerMetadataZeroSkip(); break;
            case 6: ReleasePathDiagnostics.OnFinalizerReleaseCatch(); break;
            case 7: ReleasePathDiagnostics.OnDisposeVwtDestroyInvoked(); break;
            case 8: ReleasePathDiagnostics.OnDisposeReleaseCatch(); break;
            case 9: ReleasePathDiagnostics.OnDisposeMetadataInvalidSkip(); break;
            default: throw new ArgumentOutOfRangeException(nameof(which));
        }
    }
}
