// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The types under test (JsonlTestResults, TestResults) carry their own `#nullable enable`, so the
// null-token cases below need string? parameters; this project builds with Nullable=disable +
// warnings-as-errors, where those annotations would otherwise raise CS8632.
#nullable enable

using System;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the run-identity token that makes recovered JSONL results provably belong to the
/// launch that was just attempted.
///
/// <para><b>The defect these pin.</b> The runtime-test app writes its results into its own
/// <b>persistent</b> data container, which survives reinstall on both simulator and physical
/// device. When a launch fails outright — observed for real as six consecutive
/// <c>devicectl</c> failures (CoreDeviceError 10002 / NSPOSIXErrorDomain 22 EINVAL), where the
/// process never started — the harness's sandbox copy still succeeds and hands back the
/// <em>previous</em> run's file. Every one of those six attempts logged a full
/// "3264 pass, 0 fail (done=True)" summary parsed out of one byte-identical stale artifact. That
/// run only went red by luck: the stale file predated a newly-added test class, so the class
/// inventory never emptied. Had it covered every class, the gate would have certified a green
/// device run that executed nothing.</para>
///
/// <para>The fix is validation, not mutation: the harness mints a token per launch attempt, the
/// app stamps it into the JSONL, and recovery refuses any file that does not carry it. Blanking
/// the file on the device before launch would be weaker — a push/delete that silently fails leaves
/// stale data behind indistinguishably, and it would not catch a file that simply never got
/// overwritten.</para>
/// </summary>
public class JsonlRunTokenTests
{
    const string Token = "0123456789abcdef0123456789abcdef";
    const string OtherToken = "ffffffffffffffffffffffffffffffff";

    /// <summary>A stale, fully-green results file exactly as the device bug produced it.</summary>
    static string GreenJsonl(string? runToken) =>
        (runToken == null ? "" : $"{{\"run_token\":\"{runToken}\"}}\n") +
        "{\"class\":\"AlphaTests\",\"test\":\"One\",\"status\":\"pass\",\"ms\":3}\n" +
        "{\"class_done\":\"AlphaTests\",\"tests_run\":1}\n" +
        "{\"done\":true,\"total\":1,\"passed\":1,\"failed\":0,\"skipped\":0}\n";

    // ===================================================================
    //  Token extraction
    // ===================================================================

    [Fact]
    public void ExtractRunToken_ReadsTokenLine()
    {
        Assert.Equal(Token, JsonlTestResults.ExtractRunToken(GreenJsonl(Token)));
    }

    [Fact]
    public void ExtractRunToken_ReturnsNull_WhenNoTokenLine()
    {
        Assert.Null(JsonlTestResults.ExtractRunToken(GreenJsonl(null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void ExtractRunToken_ReturnsNull_ForEmptyContent(string? content)
    {
        Assert.Null(JsonlTestResults.ExtractRunToken(content));
    }

    [Fact]
    public void ExtractRunToken_SurvivesMalformedLeadingLine()
    {
        // A truncated/garbage line must not hide a token that is present — the parser is
        // crash-safe by design and the token check has to inherit that property.
        var content = "{\"class\":\"Alpha\",\"tes\n" + GreenJsonl(Token);
        Assert.Equal(Token, JsonlTestResults.ExtractRunToken(content));
    }

    [Fact]
    public void ExtractRunToken_SurvivesCrashTruncatedTail()
    {
        // Partial JSONL from a genuinely-crashed run must stay attributable, otherwise crash
        // recovery would throw away the results it exists to salvage.
        var content =
            $"{{\"run_token\":\"{Token}\"}}\n" +
            "{\"class\":\"AlphaTests\",\"test\":\"One\",\"status\":\"pass\",\"ms\":3}\n" +
            "{\"class\":\"AlphaTests\",\"test\":\"Tw";
        Assert.Equal(Token, JsonlTestResults.ExtractRunToken(content));

        var parsed = JsonlTestResults.Parse(content);
        Assert.True(parsed.MatchesRunToken(Token));
        Assert.Single(parsed.Tests);
    }

    // ===================================================================
    //  Verification — fail-closed
    // ===================================================================

    [Fact]
    public void HasMatchingRunToken_AcceptsFileFromThisLaunch()
    {
        Assert.True(JsonlTestResults.HasMatchingRunToken(GreenJsonl(Token), Token));
    }

    [Fact]
    public void HasMatchingRunToken_RejectsStaleGreenFileFromAnEarlierLaunch()
    {
        // The exact false-green shape: a complete, all-passing, done=true file left in the
        // persistent container by a previous run, recovered after a launch that never started.
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(OtherToken), Token));
    }

    [Fact]
    public void HasMatchingRunToken_RejectsTokenlessFile()
    {
        // FAIL-CLOSED, deliberately. A file with no run_token at all — what an older app build
        // writes, and exactly what a stale pre-token artifact looks like — is rejected, never
        // "tolerated as probably ours". Tolerating it would reopen the hole this closes.
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(null), Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HasMatchingRunToken_RejectsWhenNoExpectedToken(string? expected)
    {
        // A harness that forgot to mint/plumb a token has no proof to offer, so the answer is
        // "no", not "sure". Note this also means a token-less file + a token-less expectation is
        // NOT a match — two nulls prove nothing.
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(Token), expected));
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(null), expected));
    }

    [Fact]
    public void HasMatchingRunToken_IsCaseAndWhitespaceExact()
    {
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(Token.ToUpperInvariant()), Token));
        Assert.False(JsonlTestResults.HasMatchingRunToken(GreenJsonl(Token), Token + " "));
    }

    // ===================================================================
    //  Parse integration — the token is metadata, not a test result
    // ===================================================================

    [Fact]
    public void Parse_PopulatesRunTokenWithoutDisturbingCounts()
    {
        var results = JsonlTestResults.Parse(GreenJsonl(Token));

        Assert.Equal(Token, results.RunToken);
        Assert.True(results.MatchesRunToken(Token));
        Assert.False(results.MatchesRunToken(OtherToken));

        // The token line must not be mistaken for a test record or a done summary.
        Assert.Single(results.Tests);
        Assert.Equal(1, results.PassCount);
        Assert.Equal(0, results.FailCount);
        Assert.Contains("AlphaTests", results.CompletedClasses);
        Assert.True(results.Done);
    }

    [Fact]
    public void Parse_LeavesRunTokenNull_WhenAbsent()
    {
        var results = JsonlTestResults.Parse(GreenJsonl(null));
        Assert.Null(results.RunToken);
        Assert.False(results.MatchesRunToken(Token));
    }

    [Fact]
    public void Merge_DoesNotPropagateRunToken()
    {
        // An aggregate spans several launches with different tokens; each file is validated against
        // its own launch token before it reaches Merge, so the aggregate must not appear to carry
        // one (which would be a token that certifies nothing).
        var aggregate = new JsonlTestResults();
        aggregate.Merge(JsonlTestResults.Parse(GreenJsonl(Token)));

        Assert.Null(aggregate.RunToken);
        Assert.False(aggregate.MatchesRunToken(Token));
        Assert.Single(aggregate.Tests);
    }

    // ===================================================================
    //  Round trip: the app's writer against the harness's reader
    // ===================================================================

    [Fact]
    public void AppWriter_StampsTokenThatHarnessReaderAccepts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"run-token-roundtrip-{Guid.NewGuid():N}.jsonl");
        try
        {
            var results = new RuntimeTestsApp.Infrastructure.TestResults();
            results.InitializeJsonl(path, Token);
            results.BeginClass("AlphaTests");
            results.Pass("AlphaTests.One", TimeSpan.FromMilliseconds(3));
            results.FinalizeJsonl();

            var content = File.ReadAllText(path);

            // The contract that must never drift: the writer's key/value is what the harness reads.
            Assert.True(JsonlTestResults.HasMatchingRunToken(content, Token));
            Assert.False(JsonlTestResults.HasMatchingRunToken(content, OtherToken));

            var parsed = JsonlTestResults.Parse(content);
            Assert.Equal(Token, parsed.RunToken);
            Assert.Equal(1, parsed.PassCount);
            Assert.True(parsed.Done);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void AppWriter_TokenIsFirstLine_SoItSurvivesAnEarlyCrash()
    {
        // Written and flushed before any test record: a run that dies after one test must still be
        // attributable to its launch, or crash recovery would discard salvageable results.
        var path = Path.Combine(Path.GetTempPath(), $"run-token-first-{Guid.NewGuid():N}.jsonl");
        try
        {
            var results = new RuntimeTestsApp.Infrastructure.TestResults();
            results.InitializeJsonl(path, Token);

            // No EndClass/FinalizeJsonl — simulate the process dying mid-run.
            results.BeginClass("AlphaTests");
            results.Pass("AlphaTests.One", TimeSpan.FromMilliseconds(3));

            var firstLine = File.ReadAllLines(path)[0];
            Assert.Contains("run_token", firstLine);
            Assert.Equal(Token, JsonlTestResults.ExtractRunToken(firstLine));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void AppWriter_OmitsTokenLine_WhenNoneSupplied()
    {
        // Hand-launched app (no --run-token). Nothing is stamped, and the harness — which always
        // passes a token — therefore refuses the file. That refusal is the fail-closed behavior,
        // not an oversight.
        var path = Path.Combine(Path.GetTempPath(), $"run-token-none-{Guid.NewGuid():N}.jsonl");
        try
        {
            var results = new RuntimeTestsApp.Infrastructure.TestResults();
            results.InitializeJsonl(path);
            results.BeginClass("AlphaTests");
            results.Pass("AlphaTests.One", TimeSpan.FromMilliseconds(3));
            results.FinalizeJsonl();

            var content = File.ReadAllText(path);
            Assert.DoesNotContain("run_token", content);
            Assert.False(JsonlTestResults.HasMatchingRunToken(content, Token));

            // ... while the results themselves still parse, so this is a recovery refusal and not
            // a corrupted-file problem.
            Assert.Equal(1, JsonlTestResults.Parse(content).PassCount);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void AppWriter_TruncatesPreviousContent_SoAStaleTokenCannotLinger()
    {
        // InitializeJsonl opens with append:false. Belt-and-braces alongside validation: if the app
        // DOES start, the previous run's token is gone, so the file can only ever carry one token.
        var path = Path.Combine(Path.GetTempPath(), $"run-token-truncate-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, GreenJsonl(OtherToken));

            var results = new RuntimeTestsApp.Infrastructure.TestResults();
            results.InitializeJsonl(path, Token);
            results.FinalizeJsonl();

            var content = File.ReadAllText(path);
            Assert.DoesNotContain(OtherToken, content);
            Assert.True(JsonlTestResults.HasMatchingRunToken(content, Token));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
