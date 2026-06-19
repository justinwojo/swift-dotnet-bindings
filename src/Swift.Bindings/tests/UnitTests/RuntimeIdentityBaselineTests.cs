// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="RuntimeIdentityBaseline"/> — the per-test-identity ratchet (Finding 28)
/// layered over the scalar pass-count floor in <c>CompareRuntimeBaseline</c>. The load-bearing
/// case is the net-zero swap a count-only gate misses: one test flips <c>pass → skip</c> while a
/// sibling flips <c>→ pass</c>, holding the pass count constant. Each test feeds synthetic
/// <see cref="RuntimeIdentityBaseline.TestRecord"/> lists straight into <c>Compare</c>/<c>FromResults</c>
/// (the logic is pure), mirroring the JSONL <c>(ClassName, TestName, Status)</c> the build adapts.
/// </summary>
public class RuntimeIdentityBaselineTests
{
    private const string Plat = "simulator";

    private static RuntimeIdentityBaseline.TestRecord Pass(string cls, string method)
        => new(cls, method, "pass", "");
    private static RuntimeIdentityBaseline.TestRecord Skip(string cls, string method, string reason = "")
        => new(cls, method, "skip", reason);
    private static RuntimeIdentityBaseline.TestRecord Fail(string cls, string method, string reason = "")
        => new(cls, method, "fail", reason);
    private static RuntimeIdentityBaseline.TestRecord Crash(string cls, string method, string reason = "")
        => new(cls, method, "crash", reason);

    // Builds a one-platform baseline whose floor is exactly the supplied records (as a green
    // seed run would write it).
    private static RuntimeIdentityBaseline Seeded(params RuntimeIdentityBaseline.TestRecord[] records)
        => new RuntimeIdentityBaseline().WithPlatform(Plat, RuntimeIdentityBaseline.FromResults(records));

    // ===================================================================
    //  The headline hole: net-zero pass↔skip swap
    // ===================================================================

    [Fact]
    public void Compare_PassFlipsToSkipWhileSiblingFlipsToPass_NetZero_IsRegression()
    {
        // Baseline: A.t1 pass, A.t2 pass (pass=2, skips empty).
        var baseline = Seeded(Pass("A", "t1"), Pass("A", "t2"));
        // Current: A.t2 went skip, new A.t3 passes — pass count is STILL 2.
        var current = new[] { Pass("A", "t1"), Skip("A", "t2"), Pass("A", "t3") };

        var (regressions, improvements) = baseline.Compare(Plat, current);

        // The count-only gate is blind here (2 → 2); the identity gate must catch A.t2.
        Assert.Contains(regressions, r => r.Contains("A.t2"));
        Assert.Empty(improvements);
    }

    [Fact]
    public void Compare_PassSkipSwapBetweenTwoTests_NetZero_FlagsNewSkipAndCreditsResolved()
    {
        // Baseline: A.t1 pass, A.t2 skip (pass=1). Current swaps them: A.t1 skip, A.t2 pass (pass=1).
        var baseline = Seeded(Pass("A", "t1"), Skip("A", "t2"));
        var current = new[] { Skip("A", "t1"), Pass("A", "t2") };

        var (regressions, improvements) = baseline.Compare(Plat, current);

        Assert.Contains(regressions, r => r.Contains("A.t1"));   // newly skipped
        Assert.Contains(improvements, i => i.Contains("A.t2"));  // baseline skip resolved
    }

    // ===================================================================
    //  Skip ratchet (mirrors SkipSurfaceBaseline semantics)
    // ===================================================================

    [Fact]
    public void Compare_NewSkipOnNewTest_IsRegression()
    {
        // SkipSurface semantics: a brand-new skip must be added to the baseline in the same commit.
        var baseline = Seeded(Pass("A", "t1"));
        var current = new[] { Pass("A", "t1"), Skip("B", "tNew", "documented upstream limitation") };

        var (regressions, _) = baseline.Compare(Plat, current);

        Assert.Contains(regressions, r => r.Contains("B.tNew"));
    }

    [Fact]
    public void Compare_BaselineSkipStillSkipping_NoRegression()
    {
        var baseline = Seeded(Pass("A", "t1"), Skip("A", "t2"));
        var current = new[] { Pass("A", "t1"), Skip("A", "t2") };

        var (regressions, improvements) = baseline.Compare(Plat, current);

        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    [Fact]
    public void Compare_BaselineSkipNowPasses_IsImprovementNotRegression()
    {
        var baseline = Seeded(Pass("A", "t1"), Skip("A", "t2"));
        var current = new[] { Pass("A", "t1"), Pass("A", "t2") };

        var (regressions, improvements) = baseline.Compare(Plat, current);

        Assert.Empty(regressions);
        Assert.Contains(improvements, i => i.Contains("A.t2"));
    }

    [Fact]
    public void Compare_BaselineSkipRemoved_IsImprovement()
    {
        var baseline = Seeded(Pass("A", "t1"), Skip("A", "t2"));
        var current = new[] { Pass("A", "t1") }; // A.t2 deleted

        var (regressions, improvements) = baseline.Compare(Plat, current);

        Assert.Empty(regressions);
        Assert.Contains(improvements, i => i.Contains("A.t2"));
    }

    [Fact]
    public void Compare_SkipReasonReworded_IsNotARegression()
    {
        // Identity is keyed on (Class, Method) only; the reason is audit metadata, not part of the key.
        var baseline = Seeded(Pass("A", "t1"), Skip("A", "t2", "old wording"));
        var current = new[] { Pass("A", "t1"), Skip("A", "t2", "completely new wording") };

        var (regressions, improvements) = baseline.Compare(Plat, current);

        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    // ===================================================================
    //  Scalar floor kept (pass → absent), fail/crash identities
    // ===================================================================

    [Fact]
    public void Compare_PassCountDrops_IsRegression()
    {
        // A passing test deleted with no replacement: passes are stored by count only, so the
        // scalar floor (kept in the model) is what catches this.
        var baseline = Seeded(Pass("A", "t1"), Pass("A", "t2"));
        var current = new[] { Pass("A", "t1") };

        var (regressions, _) = baseline.Compare(Plat, current);

        Assert.Contains(regressions, r => r.Contains("pass count dropped"));
    }

    [Fact]
    public void Compare_PassBecomesFail_NotBaselined_IsRegression()
    {
        var baseline = Seeded(Pass("A", "t1"), Pass("A", "t2"));
        var current = new[] { Pass("A", "t1"), Fail("A", "t2"), Pass("A", "t3") };

        var (regressions, _) = baseline.Compare(Plat, current);

        Assert.Contains(regressions, r => r.Contains("A.t2") && r.Contains("fail"));
    }

    [Fact]
    public void Compare_PassBecomesCrash_NotBaselined_IsRegression()
    {
        var baseline = Seeded(Pass("A", "t1"), Pass("A", "t2"));
        var current = new[] { Pass("A", "t1"), Crash("A", "t2"), Pass("A", "t3") };

        var (regressions, _) = baseline.Compare(Plat, current);

        Assert.Contains(regressions, r => r.Contains("A.t2") && r.Contains("crash"));
    }

    // ===================================================================
    //  Inert-until-seeded + idempotency (step 1–2 parity gate)
    // ===================================================================

    [Fact]
    public void Compare_PlatformNotSeeded_IsInert()
    {
        // Mirrors the platformKey == null early return in CompareRuntimeBaseline: wiring the gate
        // in before a platform is seeded must never produce a false red.
        var empty = new RuntimeIdentityBaseline();
        var current = new[] { Pass("A", "t1"), Skip("A", "t2"), Fail("A", "t3") };

        var (regressions, improvements) = empty.Compare("device", current);

        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    [Fact]
    public void Compare_SeededFromRun_AgainstSameRun_IsIdempotent()
    {
        // The step 1–2 parity gate as a unit test: seeding then comparing the same run is a tautology.
        var run = new[] { Pass("A", "t1"), Skip("A", "t2", "by design"), Pass("A", "t3") };
        var baseline = Seeded(run);

        var (regressions, improvements) = baseline.Compare(Plat, run);

        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    // ===================================================================
    //  FromResults dedup (Codex/Grok finding: JSONL may carry duplicate identities)
    // ===================================================================

    [Fact]
    public void FromResults_DuplicateIdentity_DedupsLastWins()
    {
        // A crash-recovery merge can leave two rows for one identity; last wins, matching the
        // AbiGrid per-runtime index. A pass-then-skip pair must NOT double-count the pass.
        var identities = RuntimeIdentityBaseline.FromResults(new[]
        {
            Pass("A", "t1"),
            Pass("A", "t1"), // duplicate identity, same status
            Pass("A", "t2"),
            Skip("A", "t2"), // later row supersedes the earlier pass
        });

        Assert.Equal(1, identities.PassCount);                      // only A.t1 counts as pass
        Assert.Single(identities.Skips);
        Assert.Equal("t2", identities.Skips[0].Method);
    }

    [Fact]
    public void FromResults_SkipsAndKnownFailsSortedDeterministically()
    {
        var identities = RuntimeIdentityBaseline.FromResults(new[]
        {
            Skip("Zeta", "tb"), Skip("Alpha", "ta"), Fail("Mid", "tc"),
        });

        Assert.Equal(new[] { "Alpha", "Zeta" }, identities.Skips.Select(s => s.Class).ToArray());
        Assert.Single(identities.KnownFails);
        Assert.Equal("Mid", identities.KnownFails[0].Class);
    }

    // ===================================================================
    //  Load/Save round-trip (source-gen JSON)
    // ===================================================================

    [Fact]
    public void SaveThenLoad_RoundTripsIdentities()
    {
        var original = Seeded(Pass("A", "t1"), Skip("A", "t2", "documented"));
        var path = Path.Combine(Path.GetTempPath(), $"rib-{System.Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            var loaded = RuntimeIdentityBaseline.Load(path);

            Assert.True(loaded.Platforms.ContainsKey(Plat));
            Assert.Equal(1, loaded.Platforms[Plat].PassCount);
            var skip = Assert.Single(loaded.Platforms[Plat].Skips);
            Assert.Equal("A", skip.Class);
            Assert.Equal("t2", skip.Method);
            Assert.Equal("documented", skip.Reason);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyInertBaseline()
    {
        var loaded = RuntimeIdentityBaseline.Load(
            Path.Combine(Path.GetTempPath(), $"rib-absent-{System.Guid.NewGuid():N}.json"));

        Assert.Empty(loaded.Platforms);
        var (regressions, _) = loaded.Compare(Plat, new[] { Skip("A", "t1") });
        Assert.Empty(regressions); // inert
    }
}
