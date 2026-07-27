// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="AsyncWrapperGate"/> — the policy every leg applies to a FAILED async-wrapper
/// build.
///
/// <para><b>The defect these pin.</b> <c>RunBuildAsyncWrapper</c> returns a success flag, and four of
/// its five callers (the iOS/device, macOS, Mac Catalyst and tvOS runtime-test legs) discarded it and
/// carried on. A Mac Catalyst leg soft-failed its wrapper build, ran against the stale wrapper, and
/// reported <c>121 passed / 1296 failed, Done: False</c> — 1296 failures produced by one missing
/// artifact. The same leg re-run after the wrapper compiled gave <c>2587 / 0 / 37</c>.</para>
///
/// <para>The load-bearing assertion is the DEFAULT: a wrapper failure is fatal unless
/// <c>--permissive</c> is passed. Warn-by-default is precisely the old behaviour, so a change that
/// flips the default must turn one of these red.</para>
/// </summary>
public class AsyncWrapperGateTests
{
    [Fact]
    public void WrapperFailure_IsFatalByDefault()
    {
        Assert.True(AsyncWrapperGate.ShouldFail(wrapperOk: false, permissive: false));
        Assert.False(AsyncWrapperGate.ShouldWarn(wrapperOk: false, permissive: false));
    }

    [Fact]
    public void WrapperFailure_IsDemotedToAWarningOnlyUnderPermissive()
    {
        Assert.False(AsyncWrapperGate.ShouldFail(wrapperOk: false, permissive: true));
        Assert.True(AsyncWrapperGate.ShouldWarn(wrapperOk: false, permissive: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulWrapperBuild_NeitherFailsNorWarns(bool permissive)
    {
        Assert.False(AsyncWrapperGate.ShouldFail(wrapperOk: true, permissive));
        Assert.False(AsyncWrapperGate.ShouldWarn(wrapperOk: true, permissive));
    }

    [Theory]
    [InlineData("iOS Simulator / device")]
    [InlineData("Mac Catalyst")]
    [InlineData("tvOS Simulator")]
    public void FailureMessage_NamesTheLegAndTheOptOut(string leg)
    {
        // The whole point is that the reader is told what actually broke instead of being handed a
        // wall of test failures, so the diagnosis must identify the leg and the escape hatch.
        var message = AsyncWrapperGate.FailureMessage(leg);

        Assert.Contains(leg, message);
        Assert.Contains("--permissive", message);
        Assert.Contains("not a test result", message);
    }

    [Fact]
    public void WarningMessage_SaysTheResultsAreNotTrustworthy()
    {
        // Proceeding under --permissive still has to warn that the run's verdict means nothing —
        // otherwise the downgrade quietly reintroduces the bug it is an opt-out from.
        var message = AsyncWrapperGate.WarningMessage("macOS");

        Assert.Contains("macOS", message);
        Assert.Contains("--permissive", message);
        Assert.Contains("NOT trustworthy", message);
    }
}
