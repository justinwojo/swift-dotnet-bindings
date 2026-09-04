// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pure-function tests for the runtime-flavor classification logic in
/// <see cref="SwiftRuntimeInfo"/> (Defect H). The classification cannot be exercised
/// directly on the unit-test host (it observes a single fixed runtime), so the decision
/// logic is factored into pure static helpers that take the build-time switch, the live
/// Mono indicator, the RID, and IsDynamicCodeSupported as explicit inputs. This lets the
/// full taxonomy matrix — including the device-Mono-without-PublishAot case that the
/// legacy heuristic misclassified — be asserted deterministically.
/// </summary>
public class SwiftRuntimeInfoClassificationTests
{
    // ── ResolveIsNativeAot: the build-time switch is authoritative ───────────────

    [Fact]
    public void NativeAot_SwitchTrue_ClassifiesAsNativeAot()
    {
        // Device + PublishAot (Direct): the SDK injects the switch as true.
        Assert.True(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: true,
            monoDetected: false, isSimulatorRid: false, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_SwitchFalse_ClassifiesAsNotNativeAot_DeviceMono()
    {
        // Defect H: iOS device, no PublishAot (Safe). IsDynamicCodeSupported=false and
        // Mono.Runtime is absent — the legacy heuristic returned true here (the bug).
        // With the authoritative switch=false the device-Mono build is NOT NativeAOT.
        Assert.False(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: false,
            monoDetected: false, isSimulatorRid: false, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_SwitchFalse_Simulator_ClassifiesAsNotNativeAot()
    {
        // Simulator with the switch injected (PackageReference consumer, Safe build).
        Assert.False(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: false,
            monoDetected: false, isSimulatorRid: true, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_SwitchAbsent_Simulator_ClassifiesAsNotNativeAot()
    {
        // Regression guard: the iOS/tvOS simulator on .NET 10 has NO Mono.Runtime type and
        // IsDynamicCodeSupported=false, so a switch-less consumer (e.g. a ProjectReference to
        // Swift.Runtime, like the test harness) hits the heuristic with the SAME inputs as the
        // ambiguous device case — distinguished ONLY by the simulator RID. NativeAOT never runs
        // on the simulator, so this must classify as NOT NativeAOT. The earlier signature lacked
        // the RID input and this branch wrongly returned true, re-enabling the direct-dispatch
        // path Defect H exists to prevent and un-skipping the [SkipOnMonoJit] tests on the sim.
        Assert.False(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: false, switchValue: false,
            monoDetected: false, isSimulatorRid: true, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_HardConflict_SwitchSaysAotButMonoIsLive_Throws()
    {
        // Build said NativeAOT but a definitive Mono runtime is present — a genuine
        // misconfiguration. Fail fast with a clear managed exception rather than take
        // the direct static-virtual path that aborts on Mono (jit-info.c:918).
        Assert.Throws<InvalidOperationException>(() => SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: true,
            monoDetected: true, isSimulatorRid: false, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_HardConflict_SwitchSaysAotButSimulatorRid_Throws()
    {
        // Build said NativeAOT but the live RID is an Apple simulator (where NativeAOT cannot
        // run). Mono.Runtime is absent on .NET 10 simulators, so the simulator RID is the only
        // conclusive Mono signal — it must still trigger the fail-fast conflict.
        Assert.Throws<InvalidOperationException>(() => SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: true,
            monoDetected: false, isSimulatorRid: true, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_HardConflict_ExceptionMessageIsTopLevel()
    {
        // R5-1b regression guard. The conflict check used to run in the eager static cctor, so
        // a throw was wrapped in TypeInitializationException — demoting the actionable
        // runtime-flavor-conflict text to .InnerException and poisoning the type for every later
        // read. The fix defers the check off the cctor (a Lazy factory), so the bare
        // InvalidOperationException surfaces with the conflict text at the TOP level. Pin that:
        // the message must be readable without unwrapping .InnerException, and the exception must
        // NOT be a TypeInitializationException.
        var ex = Assert.Throws<InvalidOperationException>(() => SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: true,
            monoDetected: true, isSimulatorRid: false, isDynamicCodeSupported: false));

        Assert.Contains("runtime-flavor conflict", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void NativeAot_SwitchAbsent_FallsBackToHeuristic_DesktopCoreClr()
    {
        // No switch (e.g. a consumer not using the SDK's buildTransitive targets).
        // Heuristic: !conclusivelyMono && !dynamicCode. Desktop CoreCLR has dynamicCode=true → not AOT.
        Assert.False(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: false, switchValue: false,
            monoDetected: false, isSimulatorRid: false, isDynamicCodeSupported: true));
    }

    [Fact]
    public void NativeAot_SwitchAbsent_FallsBackToHeuristic_MonoDetected()
    {
        Assert.False(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: false, switchValue: false,
            monoDetected: true, isSimulatorRid: false, isDynamicCodeSupported: false));
    }

    [Fact]
    public void NativeAot_SwitchAbsent_FallsBackToHeuristic_DeviceNoMonoNoDynamicCode()
    {
        // The genuinely ambiguous case the switch was introduced to resolve: absent switch +
        // (no Mono indicator) + (NOT a simulator RID — i.e. an Apple *device* rid ios-arm64) +
        // (no dynamic code) cannot be told apart from NativeAOT, so the fallback keeps the legacy
        // NativeAOT default. This is best-effort only; the SDK always injects the switch for
        // supported configs so this branch is not relied upon there. Contrast with the simulator
        // case above, which IS distinguishable via the RID.
        Assert.True(SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: false, switchValue: false,
            monoDetected: false, isSimulatorRid: false, isDynamicCodeSupported: false));
    }

    // ── ResolveIsMono: computed separately, never as !IsNativeAot ────────────────

    [Fact]
    public void Mono_NativeAot_IsNeverMono()
    {
        Assert.False(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot: true, monoDetected: false, isAppleMobileRid: true));
        Assert.False(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot: true, monoDetected: true, isAppleMobileRid: true));
    }

    [Fact]
    public void Mono_DesktopCoreClr_IsNotMono()
    {
        // Codex High #1: a non-AOT macOS CoreCLR consumer (osx-*, no Mono indicator)
        // must NOT be labeled Mono just because it is not NativeAOT.
        Assert.False(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot: false, monoDetected: false, isAppleMobileRid: false));
    }

    [Fact]
    public void Mono_AppleMobileRid_IsMono()
    {
        // iOS device/simulator, tvOS, Mac Catalyst when not NativeAOT.
        Assert.True(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot: false, monoDetected: false, isAppleMobileRid: true));
    }

    [Fact]
    public void Mono_DefinitiveMonoIndicator_IsMono()
    {
        Assert.True(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot: false, monoDetected: true, isAppleMobileRid: false));
    }

    [Fact]
    public void DeviceMonoFullAot_EndToEnd_ClassifiesAsMonoNotNativeAot()
    {
        // The exact input shape of the `nuke binding-tests --device --mono-aot` lane, and of any
        // MAUI app on a physical iPhone that did not opt into PublishAot — which is the default.
        // rid ios-arm64, no Mono.Runtime type (.NET 10+ Mono AOT has none), IsDynamicCodeSupported
        // false (the platform forbids JIT), and the build-time switch injected as false. Every
        // heuristic input here is identical to NativeAOT's; the switch is the only thing that tells
        // them apart, so this chains both resolvers rather than testing either in isolation.
        var isNativeAot = SwiftRuntimeInfo.ResolveIsNativeAot(
            switchPresent: true, switchValue: false,
            monoDetected: false, isSimulatorRid: false, isDynamicCodeSupported: false);
        Assert.False(isNativeAot);

        Assert.True(SwiftRuntimeInfo.ResolveIsMono(
            isNativeAot, monoDetected: false,
            isAppleMobileRid: SwiftRuntimeInfo.IsAppleMobileRid("ios-arm64")));
    }

    // ── IsAppleMobileRid: Apple non-desktop RIDs are Mono territory ──────────────

    [Theory]
    [InlineData("ios-arm64", true)]                 // device (Mono full-AOT or NativeAOT)
    [InlineData("iossimulator-arm64", true)]        // simulator (Mono)
    [InlineData("iossimulator-x64", true)]
    [InlineData("maccatalyst-arm64", true)]         // Catalyst (Mono)
    [InlineData("maccatalyst-x64", true)]
    [InlineData("tvos-arm64", true)]
    [InlineData("tvossimulator-arm64", true)]
    [InlineData("osx-arm64", false)]                // desktop CoreCLR
    [InlineData("osx-x64", false)]
    [InlineData("linux-x64", false)]
    [InlineData("win-x64", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppleMobileRid_ClassifiesRid(string? rid, bool expected)
    {
        Assert.Equal(expected, SwiftRuntimeInfo.IsAppleMobileRid(rid));
    }

    // ── IsSimulatorRid: Apple simulator RIDs are conclusively Mono, never NativeAOT ──

    [Theory]
    [InlineData("iossimulator-arm64", true)]
    [InlineData("iossimulator-x64", true)]
    [InlineData("tvossimulator-arm64", true)]
    [InlineData("ios-arm64", false)]                // device, NOT a simulator
    [InlineData("tvos-arm64", false)]
    [InlineData("maccatalyst-arm64", false)]
    [InlineData("osx-arm64", false)]
    [InlineData("win-x64", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSimulatorRid_ClassifiesRid(string? rid, bool expected)
    {
        Assert.Equal(expected, SwiftRuntimeInfo.IsSimulatorRid(rid));
    }
}
