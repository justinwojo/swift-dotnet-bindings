// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class LaneNativeSlicesTests : IDisposable
{
    const string Module = "SwiftBindingsTestLib";
    const string Dependency = "SwiftBindingsTestLibDependency";
    const string Wrapper = "SwiftBindings";

    readonly string root = Path.Combine(Path.GetTempPath(), "lane-native-slices-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    void Lay(string subdir, string xcframework, string framework, params string[] slices)
    {
        foreach (var slice in slices)
            Directory.CreateDirectory(Path.Combine(root, subdir, $"{xcframework}.xcframework", slice, $"{framework}.framework"));
    }

    // The shape a device regen leaves behind: the library xcframeworks keep the simulator slice
    // they are always built with, but both wrappers are rebuilt for the device only.
    void LayDeviceRegen()
    {
        Lay(".build", Module, Module, "ios-arm64-simulator", "ios-arm64");
        Lay(".build", Dependency, Dependency, "ios-arm64-simulator", "ios-arm64");
        Lay("output", Wrapper, Wrapper, "ios-arm64");
        Lay("output", Dependency + Wrapper, Dependency + Wrapper, "ios-arm64");
    }

    string[] Missing(string slice) =>
        LaneNativeSlices.Missing(LaneNativeSlices.Required(root, slice, Module, Dependency, Wrapper)).ToArray();

    [Fact]
    public void DeviceRegen_SatisfiesTheDeviceLane()
    {
        LayDeviceRegen();
        Assert.Empty(Missing("ios-arm64"));
        Assert.Null(LaneNativeSlices.Failure("Device/NativeAOT", "ios-arm64", Missing("ios-arm64"), reusedBindings: true));
    }

    [Fact]
    public void DeviceRegen_ReusedBySimulatorLane_NamesBothWrappersAndSaysRegenerate()
    {
        LayDeviceRegen();
        var missing = Missing("ios-arm64-simulator");
        Assert.Equal(2, missing.Length);
        Assert.Contains(missing, path => path.EndsWith(Path.Combine("SwiftBindings.xcframework", "ios-arm64-simulator", "SwiftBindings.framework")));
        Assert.Contains(missing, path => path.Contains($"{Dependency}{Wrapper}.xcframework"));

        var failure = LaneNativeSlices.Failure("iOS Simulator", "ios-arm64-simulator", missing, reusedBindings: true);
        Assert.NotNull(failure);
        Assert.Contains("iOS Simulator", failure);
        Assert.Contains("ios-arm64-simulator", failure);
        Assert.Contains("without --skip-regen", failure);
    }

    [Theory]
    [InlineData("macos-arm64")]
    [InlineData("ios-arm64-maccatalyst")]
    [InlineData("tvos-arm64-simulator")]
    public void AnotherPlatformsRegen_MissesEveryInputForThisLane(string slice)
    {
        LayDeviceRegen();
        Assert.Equal(4, Missing(slice).Length);
    }

    [Fact]
    public void AWrapperSliceWithoutItsFramework_IsMissing()
    {
        // A slice directory alone is what a half-finished wrapper build leaves; the app needs the framework.
        LayDeviceRegen();
        Directory.CreateDirectory(Path.Combine(root, "output", $"{Wrapper}.xcframework", "ios-arm64-simulator"));
        Assert.Contains(Missing("ios-arm64-simulator"), path => path.Contains($"{Wrapper}.xcframework"));
    }

    [Fact]
    public void FreshRegenWithoutTheSlice_IsReportedAsABuildFailureNotAStaleReuse()
    {
        var failure = LaneNativeSlices.Failure("macOS", "macos-arm64", Missing("macos-arm64"), reusedBindings: false);
        Assert.NotNull(failure);
        Assert.Contains("regeneration did not produce", failure);
        Assert.DoesNotContain("--skip-regen", failure);
    }

    [Fact]
    public void ALaneMustNameItsSlice()
        => Assert.Throws<ArgumentException>(() => LaneNativeSlices.Required(root, "", Module, Dependency, Wrapper));
}
