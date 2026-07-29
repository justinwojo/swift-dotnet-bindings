// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="GeneratorExitGate"/> — the policy a runtime-test leg applies to a NON-ZERO
/// generator exit.
///
/// <para><b>The defect these pin.</b> The desktop regeneration path logged a non-zero generator exit
/// as a warning ("this is expected if the test library includes features beyond current generator
/// support") and then built the app against bindings that were never written. One generator error
/// surfaced as roughly two thousand <c>CS0246</c>s naming binding types, so the failure read as a
/// broken binding rather than a broken generation.</para>
///
/// <para>The load-bearing assertion is the DEFAULT: a non-zero exit is fatal unless
/// <c>--permissive</c> is passed. Warn-by-default is precisely the old behaviour, so a change that
/// flips the default back must turn one of these red.</para>
/// </summary>
public class GeneratorExitGateTests
{
    [Fact]
    public void NonZeroExit_IsFatalByDefault()
    {
        Assert.True(GeneratorExitGate.ShouldFail(exitCode: 1, strict: false, permissive: false));
        Assert.False(GeneratorExitGate.ShouldWarn(exitCode: 1, strict: false, permissive: false));
    }

    [Fact]
    public void NonZeroExit_IsDemotedToAWarningOnlyUnderPermissive()
    {
        Assert.False(GeneratorExitGate.ShouldFail(exitCode: 1, strict: false, permissive: true));
        Assert.True(GeneratorExitGate.ShouldWarn(exitCode: 1, strict: false, permissive: true));
    }

    [Fact]
    public void Strict_OutranksPermissive()
    {
        // --strict is an explicit request for fail-closed behaviour; --permissive is an opt-out from
        // the default. Passing both must not produce a leg that ignores the generator, so strict
        // wins — the same precedence the compile gate's other fail-closed steps already use.
        Assert.True(GeneratorExitGate.ShouldFail(exitCode: 1, strict: true, permissive: true));
        Assert.False(GeneratorExitGate.ShouldWarn(exitCode: 1, strict: true, permissive: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ZeroExit_NeitherFailsNorWarns(bool strict, bool permissive)
    {
        // A generator that exits 0 having emitted documented skips for the members it cannot bind is
        // a supported partial binding, not a failure. This gate is about the exit code only.
        Assert.False(GeneratorExitGate.ShouldFail(exitCode: 0, strict, permissive));
        Assert.False(GeneratorExitGate.ShouldWarn(exitCode: 0, strict, permissive));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void AnyNonZeroExitCode_Fails(int exitCode)
    {
        Assert.True(GeneratorExitGate.ShouldFail(exitCode, strict: false, permissive: false));
    }

    [Theory]
    [InlineData("the macos runtime-test leg", "SwiftBindingsTestLib")]
    [InlineData("the maccatalyst runtime-test leg", "SwiftBindingsTestLibDependency")]
    public void FailureMessage_NamesTheLeg_TheModule_TheCodeAndTheOptOut(string leg, string module)
    {
        // The reader has to be told which leg and which module failed, and that the wall of compile
        // errors they would otherwise have seen was a consequence rather than the cause.
        var message = GeneratorExitGate.FailureMessage(leg, module, exitCode: 7);

        Assert.Contains(leg, message);
        Assert.Contains(module, message);
        Assert.Contains("7", message);
        Assert.Contains("CS0246", message);
        Assert.Contains("--permissive", message);
    }

    [Fact]
    public void WarningMessage_SaysTheResultsAreNotTrustworthy()
    {
        // Proceeding under --permissive still has to say the run's verdict means nothing — otherwise
        // the downgrade quietly reintroduces the behaviour it is an opt-out from.
        var message = GeneratorExitGate.WarningMessage("the macos runtime-test leg", "SwiftBindingsTestLib", exitCode: 1);

        Assert.Contains("macos", message);
        Assert.Contains("--permissive", message);
        Assert.Contains("NOT trustworthy", message);
    }
}
