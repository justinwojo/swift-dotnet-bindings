// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="MsBuildPropertyValue.Escape"/> — the command-line escaping the version
/// pipeline relies on so a <c>[X.Y.Z,)</c> floor range survives MSBuild's <c>-property:</c> parser
/// (which splits values on <c>,</c> and <c>;</c>, rejecting the trailing fragment as MSB1006). The
/// load-bearing invariants: the version-range comma escapes, and the percent escape happens first so
/// an escape this method introduces is never itself re-escaped.
/// </summary>
public class MsBuildPropertyValueTests
{
    [Fact]
    public void VersionFloorRange_CommaEscaped_SoMsBuildDoesNotSplitIt()
    {
        // The live case: [0.15.0,) must reach the property whole. The escaped form carries no bare
        // comma for the switch parser to split on, and MSBuild unescapes %2C back to ',' on read.
        Assert.Equal("[0.15.0%2C)", MsBuildPropertyValue.Escape("[0.15.0,)"));
    }

    [Theory]
    [InlineData(",", "%2C")]
    [InlineData(";", "%3B")]
    [InlineData("%", "%25")]
    public void EachListSeparatorAndPercent_IsEscaped(string input, string expected)
    {
        Assert.Equal(expected, MsBuildPropertyValue.Escape(input));
    }

    [Fact]
    public void PercentEscapedFirst_SoIntroducedEscapesAreNotDoubleEscaped()
    {
        // If comma were escaped before percent, the %2C this method emits would have its own '%'
        // turned into %25, yielding the corrupt "a%252Cb". Percent-first keeps it "a%2Cb".
        Assert.Equal("a%2Cb", MsBuildPropertyValue.Escape("a,b"));
        // A literal percent in the input still escapes exactly once.
        Assert.Equal("a%25b", MsBuildPropertyValue.Escape("a%b"));
    }

    [Fact]
    public void ValueWithoutSpecialCharacters_IsUnchanged()
    {
        Assert.Equal("0.15.0", MsBuildPropertyValue.Escape("0.15.0"));
    }

    [Fact]
    public void AllThreeHazardsTogether_EachEscapedIndependently()
    {
        Assert.Equal("a%2Cb%3Bc%25d", MsBuildPropertyValue.Escape("a,b;c%d"));
    }
}
