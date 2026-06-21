// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the console-output fallback in <see cref="JsonlTestResults"/> — the recovery path the
/// resume-on-crash loop uses when JSONL retrieval fails. Two behaviors are load-bearing for the
/// green-signal integrity gate:
///   1. The status markers are matched anywhere in a line, because real output is timestamped
///      (<c>[0.123s] [PASS] Class.Method (5ms)</c>); a leading-bracket match sees only the
///      timestamp and recovers nothing.
///   2. A console <c>[FAIL]</c> is carried out as a failure (not collapsed into "this class ran"),
///      so a failure whose JSONL was lost in the same crash cannot be excluded-as-done and then
///      certified green by a later all-pass attempt.
/// The model logic is pure, so each test feeds a synthetic console string straight into
/// <see cref="JsonlTestResults.ParseClassesFromConsole"/> / <see cref="JsonlTestResults.AddConsoleFailure"/>.
/// </summary>
public class JsonlConsoleRecoveryTests
{
    // A faithful per-test line as TestLogger emits it: "[<elapsed>s] [TAG] Class.Method ...".
    private static string Pass(string id, int ms = 5) => $"[0.123s] [PASS] {id} ({ms}ms)";
    private static string Fail(string id, string reason = "boom", int ms = 5)
        => $"[1.500s] [FAIL] {id}: {reason} ({ms}ms)";
    private static string Skip(string id, string reason = "unsupported")
        => $"[0.250s] [WARN] SKIP: {id}: {reason}";

    // ===================================================================
    //  Matching survives the timestamp prefix (the latent no-op this fixes)
    // ===================================================================

    [Fact]
    public void PassLine_IsMatchedDespiteTimestampPrefix()
    {
        // Regression guard: a first-"] " prefix match would consume the timestamp's bracket and
        // return nothing for this exact line.
        var result = JsonlTestResults.ParseClassesFromConsole(Pass("FooTests.Bar"));

        Assert.Contains("FooTests", result.CompletedClasses);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void SkipLine_RecordsClass_NoFailure()
    {
        var result = JsonlTestResults.ParseClassesFromConsole(Skip("FooTests.Bar"));

        Assert.Contains("FooTests", result.CompletedClasses);
        Assert.Empty(result.Failures);
    }

    // ===================================================================
    //  A console [FAIL] is carried out as a failure
    // ===================================================================

    [Fact]
    public void FailLine_RecordsClassAndFailureIdentity()
    {
        var result = JsonlTestResults.ParseClassesFromConsole(Fail("FooTests.Bar"));

        Assert.Contains("FooTests", result.CompletedClasses);
        Assert.Contains(("FooTests", "Bar"), result.Failures);
    }

    [Fact]
    public void FailLine_WithoutReasonOrDuration_StillCarriesFailure()
    {
        var result = JsonlTestResults.ParseClassesFromConsole("[0.100s] [FAIL] FooTests.Bar");

        Assert.Contains(("FooTests", "Bar"), result.Failures);
    }

    [Fact]
    public void DuplicateFailLines_AreDeduped()
    {
        var output = string.Join("\n", Fail("FooTests.Bar"), Fail("FooTests.Bar"));

        var result = JsonlTestResults.ParseClassesFromConsole(output);

        Assert.Single(result.Failures);
    }

    [Fact]
    public void MixedOutput_SeparatesPassFromFail()
    {
        var output = string.Join("\n",
            Pass("AlphaTests.One"),
            Fail("BetaTests.Two", "kaboom"),
            Skip("GammaTests.Three"));

        var result = JsonlTestResults.ParseClassesFromConsole(output);

        Assert.Contains("AlphaTests", result.CompletedClasses);
        Assert.Contains("BetaTests", result.CompletedClasses);
        Assert.Contains("GammaTests", result.CompletedClasses);
        Assert.Equal(new[] { ("BetaTests", "Two") }, result.Failures);
    }

    // ===================================================================
    //  Banner / summary lines are not mistaken for per-test results
    // ===================================================================

    [Fact]
    public void StatusBannerLines_AreIgnored()
    {
        var output = string.Join("\n",
            "[0.100s] [PASS] === ALL TESTS PASSED ===",
            "[0.100s] [FAIL] === SOME TESTS FAILED ===",
            "[0.100s] [FAIL]   - FooTests.Bar: earlier failure");

        var result = JsonlTestResults.ParseClassesFromConsole(output);

        // The "=== ... ===" banners have no Class.Method; the "  - ..." summary line is indented
        // with a dash, not an uppercase class name. None should register as a class or a failure.
        Assert.Empty(result.CompletedClasses);
        Assert.Empty(result.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullInput_ReturnsEmpty(string input)
    {
        var result = JsonlTestResults.ParseClassesFromConsole(input);

        Assert.Empty(result.CompletedClasses);
        Assert.Empty(result.Failures);
    }

    // ===================================================================
    //  AddConsoleFailure: the recovered failure reaches the verdict
    // ===================================================================

    [Fact]
    public void AddConsoleFailure_BumpsFailCount()
    {
        var aggregated = new JsonlTestResults();

        aggregated.AddConsoleFailure("FooTests", "Bar");

        Assert.Equal(1, aggregated.FailCount);
        Assert.Contains(aggregated.Tests, t =>
            t.ClassName == "FooTests" && t.TestName == "Bar" && t.Status == "fail");
    }

    [Fact]
    public void AddConsoleFailure_IsIdempotent()
    {
        var aggregated = new JsonlTestResults();

        aggregated.AddConsoleFailure("FooTests", "Bar");
        aggregated.AddConsoleFailure("FooTests", "Bar");

        Assert.Equal(1, aggregated.FailCount);
    }

    [Fact]
    public void RecoveredFailure_TaintsAnOtherwiseGreenAggregate()
    {
        // The exact verdict-loss shape: a later attempt passes its remaining classes (so the
        // aggregate looks green), but a class that printed [FAIL] on the crashed attempt had its
        // JSONL lost. Replaying it must leave FailCount > 0 — the signal the verdict gate keys on —
        // even though PassCount is also positive.
        var aggregated = JsonlTestResults.Parse(
            "{\"class\":\"PassingTests\",\"test\":\"Ok\",\"status\":\"pass\",\"ms\":1}");
        Assert.Equal(0, aggregated.FailCount);

        var scan = JsonlTestResults.ParseClassesFromConsole(Fail("LostTests.Gone"));
        foreach (var (cls, test) in scan.Failures)
            aggregated.AddConsoleFailure(cls, test);

        Assert.True(aggregated.PassCount > 0);
        Assert.True(aggregated.FailCount > 0);
    }
}
