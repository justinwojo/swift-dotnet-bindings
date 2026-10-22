// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.Metadata;

/// <summary>Exercises production final-app engine configuration on every runtime test host.</summary>
public class RuntimeEngineIdentityTests : TestBase
{
    public RuntimeEngineIdentityTests(TestResults results) : base(results) { }

    public void TestEngineConfigurationReachesRunningApp()
    {
        AssertTrue(AppContext.TryGetSwitch(SwiftRuntimeInfo.MonoEngineSwitchName, out bool mono),
            "Final app must transport the Mono engine switch from production Runtime targets");
        AssertTrue(AppContext.TryGetSwitch(SwiftRuntimeInfo.CoreClrEngineSwitchName, out bool coreClr),
            "Final app must transport the CoreCLR engine switch from production Runtime targets");
        AssertTrue(AppContext.TryGetSwitch(SwiftRuntimeInfo.NativeAotEngineSwitchName, out bool nativeAot),
            "Final app must transport the NativeAOT engine switch from production Runtime targets");
        AssertEqual(1, (mono ? 1 : 0) + (coreClr ? 1 : 0) + (nativeAot ? 1 : 0),
            "The final app must identify exactly one execution engine");
        AssertEqual(mono, SwiftRuntimeInfo.IsMonoRuntime, "Runtime classifier must use the final app's engine");
        AssertEqual(nativeAot, SwiftRuntimeInfo.IsNativeAotRuntime, "NativeAOT identity must follow the actual engine");
        TestLogger.Info($"Transported runtime engine: Mono={mono}, CoreCLR={coreClr}, NativeAOT={nativeAot}");
    }

    public void TestMonoSkipSelectionFollowsEngine()
    {
        AssertTrue(AppContext.TryGetSwitch(SwiftRuntimeInfo.MonoEngineSwitchName, out bool mono),
            "Mono skip selection requires a configured final-app engine");
        AssertEqual(mono, IsMonoJitRuntime, "Mono-specific skips must follow engine identity on every platform");
    }
}
