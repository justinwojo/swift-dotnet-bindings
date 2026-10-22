// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

namespace BindingsGeneration.Tests;

public class ToolchainProfileContextTests
{
    [Fact]
    public void DefaultInvocation_KeepsBaselineWritesAndLegacyEvidenceReuse()
    {
        var context = ToolchainProfileContext.Create(null, null, null);
        Assert.False(context.IsIsolated);
        Assert.True(context.CanWriteBaselines);
        context.AssertBaselineWriteAllowed("seed");
        context.AssertEvidenceCompatible(null);
    }

    [Theory]
    [InlineData(null, "toolchain", null)]
    [InlineData(null, null, "source")]
    [InlineData("net10-xcode27", null, "source")]
    [InlineData("net10-xcode27", "toolchain", null)]
    public void PartialLauncherContext_IsRejected(string profile, string toolchain, string source)
    {
        Assert.Throws<InvalidOperationException>(() => ToolchainProfileContext.Create(profile, toolchain, source));
    }

    [Fact]
    public void IsolatedInvocation_RejectsBaselineWrites()
    {
        var context = ToolchainProfileContext.Create("net10-xcode27", "toolchain", "source");
        Assert.True(context.IsIsolated);
        Assert.False(context.CanWriteBaselines);
        var error = Assert.Throws<InvalidOperationException>(() => context.AssertBaselineWriteAllowed("seed"));
        Assert.Contains("net10-xcode27", error.Message);
        Assert.Contains("canonical baselines", error.Message);
    }

    [Fact]
    public void Evidence_RequiresTheSameProfileToolchainAndSource()
    {
        var context = ToolchainProfileContext.Create("net10-xcode27", "toolchain", "source");
        context.AssertEvidenceCompatible(context.EvidenceFingerprint);
        Assert.Throws<InvalidOperationException>(() => context.AssertEvidenceCompatible(null));
        Assert.Throws<InvalidOperationException>(() => context.AssertEvidenceCompatible(
            ToolchainProfileContext.Create("net11-xcode27", "toolchain", "source").EvidenceFingerprint));
        Assert.Throws<InvalidOperationException>(() => context.AssertEvidenceCompatible(
            ToolchainProfileContext.Create("net10-xcode27", "other-toolchain", "source").EvidenceFingerprint));
        Assert.Throws<InvalidOperationException>(() => context.AssertEvidenceCompatible(
            ToolchainProfileContext.Create("net10-xcode27", "toolchain", "other-source").EvidenceFingerprint));
    }

    [Theory]
    [InlineData("seed-api-manifest-baseline", true)]
    [InlineData("SeedRuntimeIdentityBaseline", true)]
    [InlineData("SeedParityBaseline", true)]
    [InlineData("seed_skip_surface_baseline", true)]
    [InlineData("SeedWrapperStripBaseline", true)]
    [InlineData("promote-validation-baseline", true)]
    [InlineData("BindingTests", false)]
    [InlineData("Validate", false)]
    public void BaselineMutationTarget_IsRecognizedAcrossNukeSpellings(string target, bool expected)
    {
        Assert.Equal(expected, ToolchainProfileContext.IsBaselineMutationTarget(target));
    }
}
