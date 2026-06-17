// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Unit tests for <see cref="ObjCAvailabilityParser"/> (Finding 22, recovery option a2): turning the
/// raw availability annotation text recovered from header source into <see cref="ObjCAvailability"/>
/// records. These are pure string-in / records-out tests — no clang, no filesystem.
/// </summary>
public class ObjCAvailabilityParserTests
{
    // ── API_AVAILABLE ────────────────────────────────────────────────

    [Fact]
    public void ApiAvailable_MultiPlatform_IntroducedPerPlatform()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("API_AVAILABLE", "ios(13.0), macos(10.15)");

        Assert.Equal(2, result.Count);
        var ios = Assert.Single(result, a => a.Platform == "ios");
        Assert.Equal("13.0", ios.IntroducedVersion);
        Assert.False(ios.IsUnavailable);
        var macos = Assert.Single(result, a => a.Platform == "macos");
        Assert.Equal("10.15", macos.IntroducedVersion);
    }

    [Fact]
    public void ApiAvailable_UnderscoreSpelling_Recognized()
    {
        // The __API_AVAILABLE spelling (leading underscores) maps identically.
        var result = ObjCAvailabilityParser.ParseInvocation("__API_AVAILABLE", "tvos(14.0)");

        var only = Assert.Single(result);
        Assert.Equal("tvos", only.Platform);
        Assert.Equal("14.0", only.IntroducedVersion);
    }

    // ── API_UNAVAILABLE ──────────────────────────────────────────────

    [Fact]
    public void ApiUnavailable_BarePlatforms_MarkedUnavailable()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("API_UNAVAILABLE", "ios, tvos");

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.True(a.IsUnavailable));
        Assert.Contains(result, a => a.Platform == "ios");
        Assert.Contains(result, a => a.Platform == "tvos");
    }

    // ── API_DEPRECATED ───────────────────────────────────────────────

    [Fact]
    public void ApiDeprecated_MessageAndRange_IntroducedDeprecatedMessage()
    {
        var result = ObjCAvailabilityParser.ParseInvocation(
            "API_DEPRECATED", "\"use somethingElse\", ios(13.0, 15.0)");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
        Assert.Equal("13.0", only.IntroducedVersion);
        Assert.Equal("15.0", only.DeprecatedVersion);
        Assert.Equal("use somethingElse", only.Message);
    }

    [Fact]
    public void ApiDeprecatedWithReplacement_ReplacementBecomesMessage()
    {
        var result = ObjCAvailabilityParser.ParseInvocation(
            "API_DEPRECATED_WITH_REPLACEMENT", "\"newThing\", macos(10.12, 10.15)");

        var only = Assert.Single(result);
        Assert.Equal("macos", only.Platform);
        Assert.Equal("10.12", only.IntroducedVersion);
        Assert.Equal("10.15", only.DeprecatedVersion);
        Assert.Equal("newThing", only.Message);
    }

    // ── Bare __attribute__((availability(...))) keyword ──────────────

    [Fact]
    public void AttributeKeyword_AllClauses_Parsed()
    {
        var result = ObjCAvailabilityParser.ParseInvocation(
            "availability", "ios, introduced=13.0, deprecated=15.0, obsoleted=16.0, message=\"gone\"");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
        Assert.Equal("13.0", only.IntroducedVersion);
        Assert.Equal("15.0", only.DeprecatedVersion);
        Assert.Equal("16.0", only.ObsoletedVersion);
        Assert.Equal("gone", only.Message);
        Assert.False(only.IsUnavailable);
    }

    [Fact]
    public void AttributeKeyword_Unavailable_MarkedUnavailable()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("availability", "ios, unavailable");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
        Assert.True(only.IsUnavailable);
    }

    // ── NS_AVAILABLE_<PLAT> / NS_DEPRECATED_<PLAT> ──────────────────

    [Fact]
    public void NsAvailableIos_UnderscoreVersion_NormalizedToDotted()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("NS_AVAILABLE_IOS", "13_0");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
        Assert.Equal("13.0", only.IntroducedVersion);
    }

    [Fact]
    public void NsDeprecatedIos_IntroducedDeprecatedMessage()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("NS_DEPRECATED_IOS", "2_0, 9_0, \"old\"");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
        Assert.Equal("2.0", only.IntroducedVersion);
        Assert.Equal("9.0", only.DeprecatedVersion);
        Assert.Equal("old", only.Message);
    }

    [Fact]
    public void OsxDeprecatedFamily_SuffixForm_MapsToMacos()
    {
        // The __OSX_DEPRECATED family encodes the platform as a suffix on the macro core.
        var result = ObjCAvailabilityParser.ParseInvocation("__OSX_AVAILABLE", "10_12");

        var only = Assert.Single(result);
        Assert.Equal("macos", only.Platform);
        Assert.Equal("10.12", only.IntroducedVersion);
    }

    // ── Combined NS_AVAILABLE / NS_DEPRECATED (macOS, iOS) positional forms ──

    [Fact]
    public void NsAvailableCombined_MacFirstIosSecond_BothIntroduced()
    {
        // NS_AVAILABLE(_mac, _ios): positional pair, macOS first. Before the combined-form
        // handler this fell through the suffix path, read "NS" as the platform, and dropped
        // the annotation entirely.
        var result = ObjCAvailabilityParser.ParseInvocation("NS_AVAILABLE", "10_11, 9_0");

        Assert.Equal(2, result.Count);
        var mac = Assert.Single(result, a => a.Platform == "macos");
        Assert.Equal("10.11", mac.IntroducedVersion);
        var ios = Assert.Single(result, a => a.Platform == "ios");
        Assert.Equal("9.0", ios.IntroducedVersion);
    }

    [Fact]
    public void NsClassAvailableCombined_MacFirstIosSecond_BothIntroduced()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("NS_CLASS_AVAILABLE", "10_10, 8_0");

        Assert.Equal(2, result.Count);
        Assert.Equal("10.10", Assert.Single(result, a => a.Platform == "macos").IntroducedVersion);
        Assert.Equal("8.0", Assert.Single(result, a => a.Platform == "ios").IntroducedVersion);
    }

    [Fact]
    public void NsDeprecatedCombined_PositionalQuad_IntroducedDeprecatedPerPlatform()
    {
        // NS_DEPRECATED(_macIntro, _macDep, _iosIntro, _iosDep, "msg")
        var result = ObjCAvailabilityParser.ParseInvocation(
            "NS_DEPRECATED", "10_0, 10_9, 2_0, 7_0, \"use bar\"");

        Assert.Equal(2, result.Count);

        var mac = Assert.Single(result, a => a.Platform == "macos");
        Assert.Equal("10.0", mac.IntroducedVersion);
        Assert.Equal("10.9", mac.DeprecatedVersion);
        Assert.Equal("use bar", mac.Message);

        var ios = Assert.Single(result, a => a.Platform == "ios");
        Assert.Equal("2.0", ios.IntroducedVersion);
        Assert.Equal("7.0", ios.DeprecatedVersion);
        Assert.Equal("use bar", ios.Message);
    }

    [Fact]
    public void NsClassDeprecatedCombined_PositionalQuad_NoMessage()
    {
        var result = ObjCAvailabilityParser.ParseInvocation(
            "NS_CLASS_DEPRECATED", "10_4, 10_10, 2_0, 8_0");

        Assert.Equal(2, result.Count);
        var mac = Assert.Single(result, a => a.Platform == "macos");
        Assert.Equal("10.4", mac.IntroducedVersion);
        Assert.Equal("10.10", mac.DeprecatedVersion);
        Assert.Null(mac.Message);
        var ios = Assert.Single(result, a => a.Platform == "ios");
        Assert.Equal("2.0", ios.IntroducedVersion);
        Assert.Equal("8.0", ios.DeprecatedVersion);
    }

    [Fact]
    public void NsAvailableSuffix_StillRoutesToNamedPlatformPath()
    {
        // Regression guard: the exact-match combined handler must NOT intercept the
        // suffixed NS_AVAILABLE_MAC form — that one carries a single positional version
        // and must still resolve to macOS via the named-platform path.
        var result = ObjCAvailabilityParser.ParseInvocation("NS_AVAILABLE_MAC", "10_12");

        var only = Assert.Single(result);
        Assert.Equal("macos", only.Platform);
        Assert.Equal("10.12", only.IntroducedVersion);
    }

    // ── Degradation ──────────────────────────────────────────────────

    [Fact]
    public void UnknownToken_DegradesToEmpty()
    {
        var result = ObjCAvailabilityParser.ParseInvocation("SOME_PROJECT_MACRO", "whatever(1)");
        Assert.Empty(result);
    }

    [Fact]
    public void UnmappablePlatform_SkippedNotThrown()
    {
        // driverkit has no .NET binding surface → skipped, leaving only ios.
        var result = ObjCAvailabilityParser.ParseInvocation("API_AVAILABLE", "driverkit(19.0), ios(13.0)");

        var only = Assert.Single(result);
        Assert.Equal("ios", only.Platform);
    }

    [Fact]
    public void EmptyToken_ReturnsEmpty()
    {
        Assert.Empty(ObjCAvailabilityParser.ParseInvocation("", "ios(13.0)"));
        Assert.Empty(ObjCAvailabilityParser.ParseInvocation("   ", "ios(13.0)"));
    }

    [Theory]
    [InlineData("ios", "ios")]
    [InlineData("iphoneos", "ios")]
    [InlineData("macos", "macos")]
    [InlineData("macosx", "macos")]
    [InlineData("osx", "macos")]
    [InlineData("tvos", "tvos")]
    [InlineData("watchos", "watchos")]
    [InlineData("maccatalyst", "maccatalyst")]
    [InlineData("uikitformac", "maccatalyst")]
    [InlineData("visionos", "visionos")]
    [InlineData("xros", "visionos")]
    public void MapPlatform_KnownSpellings_MapToDotnetPlatform(string objc, string expected)
    {
        Assert.Equal(expected, ObjCAvailabilityParser.MapPlatform(objc));
    }

    [Theory]
    [InlineData("driverkit")]
    [InlineData("swift")]
    [InlineData("bogus")]
    public void MapPlatform_UnknownSpellings_ReturnNull(string objc)
    {
        Assert.Null(ObjCAvailabilityParser.MapPlatform(objc));
    }

    [Fact]
    public void SplitTopLevelArgs_IgnoresCommasInsideParensAndStrings()
    {
        var parts = ObjCAvailabilityParser.SplitTopLevelArgs("ios(13.0, 15.0), \"a, b\", macos(10.15)");
        Assert.Equal(3, parts.Count);
        Assert.Equal("ios(13.0, 15.0)", parts[0]);
        Assert.Equal("\"a, b\"", parts[1]);
        Assert.Equal("macos(10.15)", parts[2]);
    }
}
