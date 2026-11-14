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
    [Fact]
    public void Net10FloorsAreSeededOnlyOutsideAProfile()
    {
        var lane = RuntimeTestLane.Create(null, null, null, false, true, false);
        Assert.Equal("runtime-identity-baseline.json", lane.IdentityBaselineFile);
        Assert.Null(lane.IdentitySeedRefusal(isolatedProfile: false, lane.Configuration));
        Assert.Contains("never writes the .NET 10", lane.IdentitySeedRefusal(isolatedProfile: true, lane.Configuration));
    }

    [Theory]
    // Simulator / host / CoreCLR device lanes build the requested configuration. Debug lanes are
    // eligible inside the profile; the run then has to prove them (RuntimeIdentitySeedPolicyTests).
    [InlineData(false, false, "Release", true, true)]
    [InlineData(true, true, "Release", true, true)]
    [InlineData(false, false, "Debug", true, true)]
    [InlineData(true, true, "Debug", true, true)]
    // NativeAOT device publishes Release whatever was requested.
    [InlineData(true, false, "Release", true, true)]
    // Outside the net11 launcher nothing .NET 11 is seedable, Release or Debug.
    [InlineData(false, false, "Release", false, false)]
    [InlineData(true, false, "Release", false, false)]
    [InlineData(false, false, "Debug", false, false)]
    [InlineData(true, true, "Debug", false, false)]
    public void Net11FloorsAreEligibleOnlyInsideTheProfile(bool device, bool coreClr, string configuration,
        bool isolated, bool allowed)
    {
        var lane = RuntimeTestLane.Create("net11.0", "net11.0", configuration, coreClr, device, false);
        Assert.Equal("runtime-identity-net11-baseline.json", lane.IdentityBaselineFile);
        var refusal = lane.IdentitySeedRefusal(isolated, lane.BuildConfiguration(device, false));
        Assert.Equal(allowed, refusal == null);
        if (!allowed)
            Assert.Contains("net11 toolchain profile launcher", refusal);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void Net11FloorNeedsTheWholeSuite(string configuration)
    {
        // A filter naming only a negative control grades the control and never reaches the seed,
        // so the request is refused before anything runs.
        var lane = RuntimeTestLane.Create("net11.0", "net11.0", configuration, false, false, false);
        Assert.Contains("--class-filter", lane.IdentitySeedRefusal(isolatedProfile: true, configuration, classFilter: true));
    }

    [Theory]
    [InlineData("Debug", false)]
    [InlineData("Release", true)]
    public void MacOSHasNoDebugInterpreterFloor(string configuration, bool allowed)
    {
        // The macOS runtime has no interpreter: a Debug app runs the JIT and proves no lane.
        var lane = RuntimeTestLane.Create("net11.0", "net11.0", configuration, false, false, false);
        var refusal = lane.IdentitySeedRefusal(isolatedProfile: true, configuration, macOS: true);
        Assert.Equal(allowed, refusal == null);
        if (!allowed)
            Assert.Contains("JIT", refusal);
    }

    [Theory]
    [InlineData("simulator", "iossimulator-arm64", false, false, "net11.0_simulator_iossimulator-arm64_coreclr_release")]
    [InlineData("maccatalyst", "maccatalyst-arm64", false, false, "net11.0_maccatalyst_maccatalyst-arm64_coreclr_release")]
    [InlineData("tvos_simulator", "tvossimulator-arm64", false, false, "net11.0_tvos_simulator_tvossimulator-arm64_coreclr_release")]
    [InlineData("device", "ios-arm64", true, true, "net11.0_device_ios-arm64_coreclr_release")]
    public void DebugFloorIsCheckedAgainstTheSameEngineReleaseFloor(string platform, string rid, bool device, bool coreClr, string release)
    {
        var lane = RuntimeTestLane.Create("net11.0", "net11.0", "Debug", coreClr, device, false);
        Assert.EndsWith("_debug", lane.BaselineKey(platform, rid, device));
        Assert.Equal(release, lane.ReleaseSiblingKey(platform, rid, device));
    }

    [Theory]
    // No auxiliary gate requested: the plain compile-only gate (and every runtime lane) runs on net11.
    [InlineData("net11.0", false, false, false, false, true)]
    [InlineData("net11.0", true, false, false, false, false)]
    [InlineData("net11.0", false, true, false, false, false)]
    [InlineData("net11.0", false, false, true, false, false)]
    [InlineData("net11.0", false, false, false, true, false)]
    // .NET 10 owns every auxiliary gate.
    [InlineData("net10.0", true, true, true, true, true)]
    public void Net11RefusesOnlyTheGatesThatKeepNet10Policy(string framework, bool abiGrid, bool appleSmoke,
        bool pushToken, bool catalystX64Jit, bool runs)
    {
        var lane = RuntimeTestLane.Create(framework, null, null, false, false, false);
        var refusal = lane.AuxiliaryGateRefusal(abiGrid, appleSmoke, pushToken, catalystX64Jit);
        Assert.Equal(runs, refusal == null);
        if (refusal != null)
            Assert.DoesNotContain("Compile-only", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Net11ReleaseFloorNeedsAnExplicitReleaseRequest()
    {
        // The NativeAOT device lane publishes Release whatever was requested; a Debug request,
        // which may seed Debug lanes, must not write its Release floor.
        var debugRequest = RuntimeTestLane.Create("net11.0", "net11.0", null, false, true, false);
        Assert.Contains("explicit --binding-test-configuration Release",
            debugRequest.IdentitySeedRefusal(isolatedProfile: true, debugRequest.BuildConfiguration(true, false)));
        Assert.Null(debugRequest.IdentitySeedRefusal(isolatedProfile: true, debugRequest.BuildConfiguration(false, false)));
        var releaseRequest = RuntimeTestLane.Create("net11.0", "net11.0", "Release", false, true, false);
        Assert.Null(releaseRequest.IdentitySeedRefusal(isolatedProfile: true, releaseRequest.BuildConfiguration(true, false)));
    }
}
