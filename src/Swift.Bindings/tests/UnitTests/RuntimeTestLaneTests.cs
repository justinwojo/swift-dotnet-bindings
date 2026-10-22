// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using Xunit;

namespace BindingsGeneration.Tests;

public class RuntimeTestLaneTests
{
    [Fact]
    public void LegacyDefaultRetainsTargetAndDevicePublishConfiguration()
    {
        var lane = RuntimeTestLane.Create(null, null, null, false, true, false);
        Assert.Equal("net10.0", lane.Framework);
        Assert.Equal("Debug", lane.Configuration);
        Assert.Equal("Release", lane.BuildConfiguration(true, false));
        Assert.Equal("bin", lane.AppBinDirectory(true));
    }

    [Fact]
    public void ProfileSuppliesFrameworkAndExplicitMismatchFails()
    {
        Assert.Equal("net11.0", RuntimeTestLane.Create(null, "net11.0", null, false, false, false).Framework);
        Assert.Throws<ArgumentException>(() => RuntimeTestLane.Create("net10.0", "net11.0", null, false, false, false));
    }

    [Theory]
    [InlineData("net12.0", "Debug", false, false, false)]
    [InlineData("net11.0-ios", "Debug", false, false, false)]
    [InlineData("net10.0", "Release", false, false, false)]
    [InlineData("net11.0", "Profile", false, false, false)]
    [InlineData("net11.0", "Debug", false, true, true)]
    [InlineData("net11.0", "Debug", true, false, false)]
    [InlineData("net10.0", "Debug", true, true, false)]
    [InlineData("net10.0", "Debug", false, false, true)]
    public void UnsupportedCombinationsFailBeforeBuild(string framework, string configuration, bool coreClr, bool device, bool monoAot)
        => Assert.Throws<ArgumentException>(() => RuntimeTestLane.Create(framework, null, configuration, coreClr, device, monoAot));

    [Fact]
    public void NewFrameworkEngineConfigurationAndArchitectureHaveDistinctAuthorities()
    {
        var coreClr = RuntimeTestLane.Create("net11.0", null, "Release", true, true, false);
        var debug = coreClr with { Configuration = "Debug" };
        var nativeAot = coreClr with { DeviceCoreClr = false };
        Assert.NotEqual(coreClr.BaselineKey("device", "ios-arm64", true), nativeAot.BaselineKey("device", "ios-arm64", true));
        Assert.NotEqual(coreClr.BaselineKey("device", "ios-arm64", true), debug.BaselineKey("device", "ios-arm64", true));
        Assert.NotEqual(coreClr.BaselineKey("macos", "osx-arm64", false), coreClr.BaselineKey("macos", "osx-x64", false));
        Assert.NotEqual(coreClr.AppBinDirectory(true), nativeAot.AppBinDirectory(true));
        Assert.Contains("net11.0", coreClr.BaselineKey("device", "ios-arm64", true));
    }

    [Theory]
    [InlineData("net10.0", "ios")]
    [InlineData("net10.0", "macos")]
    [InlineData("net10.0", "maccatalyst")]
    [InlineData("net10.0", "tvos")]
    [InlineData("net11.0", "ios")]
    [InlineData("net11.0", "macos")]
    [InlineData("net11.0", "maccatalyst")]
    [InlineData("net11.0", "tvos")]
    public void HarnessGeneratorTargetIsAcceptedByActualGenerator(string framework, string platform)
    {
        var harnessPlatform = global::ApplePlatform.FromName(platform);
        var target = harnessPlatform.GetTfm(framework);
        var generated = PlatformInfoFactory.Create(PlatformInfoFactory.ParsePlatform(platform)!.Value, null, target);
        Assert.Equal(target, generated.Tfm);
        Assert.StartsWith(framework + "-", generated.Tfm);
    }

    [Fact]
    public void LegacyBindingsCannotBeReusedAsNewTarget()
    {
        RuntimeTestLane.AssertFrameworkStamp("net10.0", null);
        RuntimeTestLane.AssertFrameworkStamp("net11.0", "net11.0");
        Assert.Throws<InvalidOperationException>(() => RuntimeTestLane.AssertFrameworkStamp("net11.0", null));
        Assert.Throws<InvalidOperationException>(() => RuntimeTestLane.AssertFrameworkStamp("net10.0", "net11.0"));
    }

    [Theory]
    [InlineData("False", "False", "v11.0", true)]
    [InlineData("True", "False", "v11.0", false)]
    [InlineData("False", "True", "v11.0", false)]
    [InlineData("False", "False", "v10.0", false)]
    public void CoreClrRequiresLiveFrameworkAndEngineRegardlessOfDynamicCode(string mono, string nativeAot, string framework, bool match)
    {
        var lane = RuntimeTestLane.Create("net11.0", null, null, true, true, false);
        var output = $"Runtime flavor: IsMonoRuntime={mono}, IsNativeAotRuntime={nativeAot}, IsDynamicCodeSupported=False, Framework=.NETCoreApp,Version={framework}, Runtime=.NET 11";
        Assert.Equal(match, lane.MatchesLiveIdentity(output, true));
        Assert.False(lane.MatchesLiveIdentity("TEST SUCCESS", true));
    }

    [Fact]
    public void NativeAotRequiresPositiveLiveAotEvidence()
    {
        var lane = RuntimeTestLane.Create("net11.0", null, null, false, true, false);
        Assert.True(lane.MatchesLiveIdentity("Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=True, Framework=.NETCoreApp,Version=v11.0, Runtime=.NET 11", true));
        Assert.False(lane.MatchesLiveIdentity("Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=False, Framework=.NETCoreApp,Version=v11.0, Runtime=.NET 11", true));
    }
}
