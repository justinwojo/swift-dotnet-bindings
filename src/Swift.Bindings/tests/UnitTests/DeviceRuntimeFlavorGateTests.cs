// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// DeviceRuntimeFlavorGate carries its own `#nullable enable` (a null/absent banner is a real input),
// so the cases below need string? parameters; this project builds with Nullable=disable +
// warnings-as-errors, where the annotation would otherwise raise CS8632.
#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="DeviceRuntimeFlavorGate"/> — the check that a physical-device runtime-test
/// run really happened on the runtime its lane is named after.
///
/// <para><b>The defect these pin.</b> <c>--device</c> (NativeAOT) and <c>--device --mono-aot</c>
/// (Mono full-AOT, the .NET-for-iOS default and what a MAUI app ships on) share one bundle id, one
/// install path and one test suite; only an MSBuild property separates them. When that property
/// stops taking effect nothing downstream complains — the app builds, installs, launches and goes
/// green — so the harness would certify a second NativeAOT run as coverage of the Mono runtime.
/// A gate that can only ever pass is not a gate, so the mismatch arms are tested as first-class
/// cases here rather than being left to a phone that would have to be misconfigured to exercise
/// them.</para>
/// </summary>
public class DeviceRuntimeFlavorGateTests
{
    private const string MonoAotBanner =
        "Runtime flavor: IsMonoRuntime=True, IsNativeAotRuntime=False, IsMonoAot=True, Rid=ios-arm64";

    private const string NativeAotBanner =
        "Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=True, IsMonoAot=False, Rid=ios-arm64";

    private const string SimulatorBanner =
        "Runtime flavor: IsMonoRuntime=True, IsNativeAotRuntime=False, IsMonoAot=False, Rid=iossimulator-arm64";

    private static string Console(string banner) =>
        "=== RUNTIME TESTS ===\nPlatform: DeviceMonoAot\n" + banner + "\nStarted at 10:00:00\n";

    [Fact]
    public void MonoAotLane_MonoAotBanner_Matches()
    {
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Match,
            DeviceRuntimeFlavorGate.Judge(Console(MonoAotBanner), monoAotLane: true));
    }

    [Fact]
    public void NativeAotLane_NativeAotBanner_Matches()
    {
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Match,
            DeviceRuntimeFlavorGate.Judge(Console(NativeAotBanner), monoAotLane: false));
    }

    [Fact]
    public void MonoAotLane_NativeAotBanner_IsMismatch()
    {
        // The headline failure: -p:SwiftBindingsDeviceMonoAot=true did not suppress PublishAot, so
        // the "Mono" lane published NativeAOT and would otherwise have gone green.
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Mismatch,
            DeviceRuntimeFlavorGate.Judge(Console(NativeAotBanner), monoAotLane: true));
    }

    [Fact]
    public void MonoAotLane_MonoRuntimeButMisclassifiedAsNativeAot_IsMismatch()
    {
        // The second, quieter failure: the app lost the Swift.Runtime.IsNativeAot AppContext switch,
        // so SwiftRuntimeInfo's switch-less heuristic — which cannot tell device Mono full-AOT from
        // NativeAOT — defaulted to NativeAOT and armed the Direct dispatch path on Mono.
        const string misclassified =
            "Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=True, IsMonoAot=False, Rid=ios-arm64";
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Mismatch,
            DeviceRuntimeFlavorGate.Judge(Console(misclassified), monoAotLane: true));
    }

    [Fact]
    public void NativeAotLane_MonoBanner_IsMismatch()
    {
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Mismatch,
            DeviceRuntimeFlavorGate.Judge(Console(MonoAotBanner), monoAotLane: false));
    }

    [Fact]
    public void MonoAotLane_SimulatorBanner_IsMismatch()
    {
        // Mono, but not on a phone — IsMonoAot is false, so the Mono *device* lane must not accept it.
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.Mismatch,
            DeviceRuntimeFlavorGate.Judge(Console(SimulatorBanner), monoAotLane: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("=== RUNTIME TESTS ===\nPlatform: DeviceMonoAot\n")]
    public void NoBanner_YieldsNoEvidence(string? output)
    {
        // A launch that produced no banner is a launch/crash story the recovery loop classifies.
        // Reporting a flavor mismatch here would replace that diagnosis with a wrong one.
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.NoEvidence,
            DeviceRuntimeFlavorGate.Judge(output, monoAotLane: true));
        Assert.Equal(
            DeviceRuntimeFlavorGate.Verdict.NoEvidence,
            DeviceRuntimeFlavorGate.Judge(output, monoAotLane: false));
        Assert.Null(DeviceRuntimeFlavorGate.ExtractBanner(output));
    }

    [Fact]
    public void ExtractBanner_ReturnsOnlyTheBannerLine()
    {
        Assert.Equal(
            "IsMonoRuntime=True, IsNativeAotRuntime=False, IsMonoAot=True, Rid=ios-arm64",
            DeviceRuntimeFlavorGate.ExtractBanner(Console(MonoAotBanner)));
    }

    [Fact]
    public void MismatchMessage_NamesTheLaneAndTheBanner()
    {
        var message = DeviceRuntimeFlavorGate.MismatchMessage(
            "Device/MonoAOT",
            "IsMonoRuntime=False, IsNativeAotRuntime=True, IsMonoAot=False, Rid=ios-arm64",
            monoAotLane: true);

        Assert.Contains("Device/MonoAOT", message);
        Assert.Contains("IsNativeAotRuntime=True", message);
        Assert.Contains("SwiftBindingsDeviceMonoAot", message);
    }
}
