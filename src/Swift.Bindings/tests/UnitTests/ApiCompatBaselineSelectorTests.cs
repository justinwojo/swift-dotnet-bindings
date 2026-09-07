// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ApiCompatBaselineSelector"/> — the pack-time choice of which shipped
/// <c>SwiftBindings.Runtime</c> the new pack's public surface must be additive over. The
/// load-bearing invariants: the baseline is the highest STABLE release strictly below the pack
/// version (so a removed member is caught against what consumers actually compiled against),
/// prerelease tags never serve as baselines, and a pack cannot run against itself.
/// </summary>
public class ApiCompatBaselineSelectorTests
{
    static readonly string[] ReleaseTags =
    {
        "sdk-v0.17.0", "sdk-v0.18.0", "sdk-v0.18.1", "sdk-v0.19.0", "sdk-v0.19.1",
        "sdk-v0.19.2", "sdk-v0.19.3", "sdk-v0.19.4", "apple-v26.2.8", "apple-v26.2.7",
    };

    [Theory]
    [InlineData("0.20.0", "0.19.4")]
    [InlineData("0.19.5", "0.19.4")]
    [InlineData("1.0.0", "0.19.4")]
    public void PicksHighestStableTagBelowPackVersion(string packVersion, string expected)
    {
        Assert.Equal(expected, ApiCompatBaselineSelector.Select(ReleaseTags, packVersion));
    }

    [Fact]
    public void RepackOfAnAlreadyTaggedVersion_DiffsAgainstItsPredecessor_NeverItself()
    {
        // A local rebuild of a shipped version must not be compared with itself: that diff is
        // vacuously additive and would hide a member removed since the previous release.
        Assert.Equal("0.19.3", ApiCompatBaselineSelector.Select(ReleaseTags, "0.19.4"));
    }

    [Fact]
    public void PrereleaseTags_AreNeverBaselines()
    {
        var tags = new[] { "sdk-v0.19.4", "sdk-v0.20.0-preview.1", "sdk-v0.20.0-rc.2" };
        Assert.Equal("0.19.4", ApiCompatBaselineSelector.Select(tags, "0.20.0"));
        // ...even when the prerelease is the only tag numerically closer to the pack.
        Assert.Equal("0.19.4", ApiCompatBaselineSelector.Select(tags, "0.20.1"));
    }

    [Fact]
    public void PrereleasePackVersion_UsesItsReleaseCore()
    {
        // 0.20.0-preview.1 is being diffed against the last stable below 0.20.0, not rejected.
        Assert.Equal("0.19.4", ApiCompatBaselineSelector.Select(ReleaseTags, "0.20.0-preview.1"));
    }

    [Fact]
    public void OrdersNumerically_NotLexically()
    {
        // Lexical ordering would rank 0.9.0 above 0.19.4; the baseline must be the numeric maximum.
        var tags = new[] { "sdk-v0.9.0", "sdk-v0.19.4", "sdk-v0.10.0" };
        Assert.Equal("0.19.4", ApiCompatBaselineSelector.Select(tags, "0.20.0"));
    }

    [Fact]
    public void IgnoresTagsOutsideTheSdkLane_AndMalformedOnes()
    {
        var tags = new[] { "apple-v26.2.8", "v0.19.9", "sdk-v0.19", "sdk-vlatest", "  sdk-v0.18.0  " };
        Assert.Equal("0.18.0", ApiCompatBaselineSelector.Select(tags, "0.20.0"));
    }

    [Fact]
    public void NoQualifyingTag_ReturnsNull_SoTheCallerFailsLoudly()
    {
        Assert.Null(ApiCompatBaselineSelector.Select(Array.Empty<string>(), "0.20.0"));
        Assert.Null(ApiCompatBaselineSelector.Select(new[] { "sdk-v0.20.0", "sdk-v0.21.0" }, "0.20.0"));
    }

    [Theory]
    [InlineData("0.20")]
    [InlineData("0.20.0.0")]
    [InlineData("latest")]
    public void MalformedPackVersion_Throws(string packVersion)
    {
        Assert.Throws<InvalidOperationException>(() => ApiCompatBaselineSelector.Select(ReleaseTags, packVersion));
    }

    [Theory]
    [InlineData("0.20.0", "0.20.0")]
    [InlineData("0.20.0-preview.1", "0.20.0")]
    [InlineData("0.20.0+build.7", "0.20.0")]
    public void ParseReleaseCore_DropsPrereleaseAndBuildSuffixes(string version, string expectedCore)
    {
        Assert.Equal(Version.Parse(expectedCore), ApiCompatBaselineSelector.ParseReleaseCore(version));
    }
}
