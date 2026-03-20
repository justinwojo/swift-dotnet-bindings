// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests that frozen structs with float/bool/>8byte fields correctly route through
/// @_cdecl wrappers (RequiresCdeclForAbiSafety). If the generator doesn't flag
/// these correctly, Mono JIT crashes with SIGSEGV on simulator.
///
/// Coverage gaps addressed:
/// - Float fields → @_cdecl required (IsSelfTypeCdeclRequired:877)
/// - Bool fields → @_cdecl required (IsSelfTypeCdeclRequired:881)
/// - >8 bytes → @_cdecl required (IsSelfTypeCdeclRequired:888)
/// - Non-frozen instance member (RequiresCdeclForAbiSafety:713)
///
/// Real-world failures: Lottie LottieColor (float SIGSEGV), Alamofire/Kingfisher/DeviceKit crashes.
/// </summary>
public class AbiSafetyRuntimeTests : TestBase
{
    public AbiSafetyRuntimeTests(TestResults results) : base(results) { }

    #region LottieColorLike — Frozen Struct with Float Fields

    public void TestLottieColorLikeConstruction()
    {
        var color = new LottieColorLike(r: 1.0, g: 0.5, b: 0.25, a: 1.0);
        AssertApproxEqual(1.0, color.R, message: "R field");
        AssertApproxEqual(0.5, color.G, message: "G field");
        AssertApproxEqual(0.25, color.B, message: "B field");
        AssertApproxEqual(1.0, color.A, message: "A field");
        TestLogger.Info("LottieColorLike construction + field access passed");
    }

    public void TestLottieColorLikePropertyRoundTrip()
    {
        var color = new LottieColorLike(r: 0.0, g: 0.0, b: 0.0, a: 0.0);
        color.R = 0.9;
        color.G = 0.6;
        color.B = 0.3;
        color.A = 1.0;
        AssertApproxEqual(0.9, color.R, message: "R after set");
        AssertApproxEqual(0.6, color.G, message: "G after set");
        AssertApproxEqual(0.3, color.B, message: "B after set");
        AssertApproxEqual(1.0, color.A, message: "A after set");
        TestLogger.Info("LottieColorLike property round-trip passed");
    }

    public void TestLottieColorLikeBrightness()
    {
        var color = new LottieColorLike(r: 0.9, g: 0.6, b: 0.3, a: 1.0);
        var brightness = color.GetBrightness();
        AssertApproxEqual(0.6, brightness, message: "Brightness = (0.9+0.6+0.3)/3");
        TestLogger.Info($"LottieColorLike.GetBrightness() = {brightness}");
    }

    public void TestLottieColorLikeWithAlpha()
    {
        var color = new LottieColorLike(r: 1.0, g: 0.0, b: 0.0, a: 1.0);
        var transparent = color.WithAlpha(0.5);
        AssertApproxEqual(0.5, transparent.A, message: "New alpha");
        AssertApproxEqual(1.0, transparent.R, message: "R preserved");
        AssertApproxEqual(0.0, transparent.G, message: "G preserved");
        AssertApproxEqual(0.0, transparent.B, message: "B preserved");
        TestLogger.Info("LottieColorLike.WithAlpha passed");
    }

    public void TestLottieColorLikeDescribe()
    {
        var color = new LottieColorLike(r: 1.0, g: 0.5, b: 0.0, a: 1.0);
        var desc = color.GetDescribe();
        AssertTrue(desc.Contains("1.0"), "Description contains R value");
        TestLogger.Info($"LottieColorLike.GetDescribe() = {desc}");
    }

    #endregion

    #region FeatureFlags — Frozen Struct with Bool Fields

    public void TestFeatureFlagsConstruction()
    {
        var flags = new FeatureFlags(enableLogging: true, enableCache: false, debugMode: true);
        AssertTrue(flags.EnableLogging, "EnableLogging");
        AssertFalse(flags.EnableCache, "EnableCache");
        AssertTrue(flags.DebugMode, "DebugMode");
        TestLogger.Info("FeatureFlags construction + field access passed");
    }

    public void TestFeatureFlagsPropertyRoundTrip()
    {
        var flags = new FeatureFlags(enableLogging: false, enableCache: false, debugMode: false);
        flags.EnableLogging = true;
        flags.EnableCache = true;
        flags.DebugMode = false;
        AssertTrue(flags.EnableLogging, "EnableLogging after set");
        AssertTrue(flags.EnableCache, "EnableCache after set");
        AssertFalse(flags.DebugMode, "DebugMode after set");
        TestLogger.Info("FeatureFlags property round-trip passed");
    }

    public void TestFeatureFlagsActiveCount()
    {
        var flags = new FeatureFlags(enableLogging: true, enableCache: false, debugMode: true);
        var count = flags.GetActiveCount();
        AssertEqual(2, count, "2 flags active");
        TestLogger.Info($"FeatureFlags.GetActiveCount() = {count}");
    }

    public void TestFeatureFlagsAllEnabled()
    {
        var allOn = new FeatureFlags(enableLogging: true, enableCache: true, debugMode: true);
        AssertTrue(allOn.GetAllEnabled(), "All enabled");

        var partial = new FeatureFlags(enableLogging: true, enableCache: false, debugMode: true);
        AssertFalse(partial.GetAllEnabled(), "Not all enabled");
        TestLogger.Info("FeatureFlags.GetAllEnabled passed");
    }

    public void TestFeatureFlagsDescribe()
    {
        var flags = new FeatureFlags(enableLogging: true, enableCache: false, debugMode: false);
        var desc = flags.GetDescribe();
        AssertTrue(desc.Contains("true"), "Description contains true");
        TestLogger.Info($"FeatureFlags.GetDescribe() = {desc}");
    }

    #endregion

    #region LargeConfig — Frozen Struct >8 Bytes

    public void TestLargeConfigConstruction()
    {
        var config = new LargeConfig(width: 10, height: 20, depth: 30);
        AssertEqual((nint)10, config.Width, "Width");
        AssertEqual((nint)20, config.Height, "Height");
        AssertEqual((nint)30, config.Depth, "Depth");
        TestLogger.Info("LargeConfig construction + field access passed");
    }

    public void TestLargeConfigPropertyRoundTrip()
    {
        var config = new LargeConfig(width: 1, height: 2, depth: 3);
        config.Width = 100;
        config.Height = 200;
        config.Depth = 300;
        AssertEqual((nint)100, config.Width, "Width after set");
        AssertEqual((nint)200, config.Height, "Height after set");
        AssertEqual((nint)300, config.Depth, "Depth after set");
        TestLogger.Info("LargeConfig property round-trip passed");
    }

    public void TestLargeConfigVolume()
    {
        var config = new LargeConfig(width: 10, height: 20, depth: 30);
        var volume = config.GetVolume();
        AssertEqual((nint)6000, volume, "Volume = 10*20*30");
        TestLogger.Info($"LargeConfig.GetVolume() = {volume}");
    }

    public void TestLargeConfigSurfaceArea()
    {
        var config = new LargeConfig(width: 2, height: 3, depth: 4);
        var area = config.GetSurfaceArea();
        // 2*(2*3 + 3*4 + 2*4) = 2*(6 + 12 + 8) = 52
        AssertEqual((nint)52, area, "Surface area");
        TestLogger.Info($"LargeConfig.GetSurfaceArea() = {area}");
    }

    public void TestLargeConfigDescribe()
    {
        var config = new LargeConfig(width: 5, height: 10, depth: 15);
        var desc = config.GetDescribe();
        AssertTrue(desc.Contains("5"), "Description contains width");
        AssertTrue(desc.Contains("10"), "Description contains height");
        AssertTrue(desc.Contains("15"), "Description contains depth");
        TestLogger.Info($"LargeConfig.GetDescribe() = {desc}");
    }

    #endregion

    #region FlexibleConfig — Non-Frozen Struct with Instance Methods

    public void TestFlexibleConfigShouldRetry()
    {
        var config = new FlexibleConfig(name: "api", retryCount: 3);
        var should = config.GetShouldRetry();
        AssertTrue(should, "Should retry when count > 0");
        TestLogger.Info("FlexibleConfig.GetShouldRetry passed");
    }

    public void TestFlexibleConfigShouldRetryZero()
    {
        var config = new FlexibleConfig(name: "api", retryCount: 0);
        var should = config.GetShouldRetry();
        AssertFalse(should, "Should not retry when count = 0");
        TestLogger.Info("FlexibleConfig.GetShouldRetry(0) passed");
    }

    public void TestFlexibleConfigDescribe()
    {
        var config = new FlexibleConfig(name: "api", retryCount: 3);
        var desc = config.GetDescribe();
        AssertTrue(desc.Contains("api"), "Description contains name");
        AssertTrue(desc.Contains("3"), "Description contains retry count");
        TestLogger.Info($"FlexibleConfig.GetDescribe() = {desc}");
    }

    #endregion
}
