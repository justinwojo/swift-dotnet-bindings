// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
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

    #region MultiInitClass — Class Allocating Constructor @_cdecl Wrappers

    public void TestMultiInitClassParameterless()
    {
        // Tests parameterless class constructor gets @_cdecl wrapper.
        // Previously crashed Mono JIT: Keychain(), MD5() pattern (hidden metatype + CallConvSwift).
        var obj = new MultiInitClass();
        AssertEqual("default", obj.Label.ToString(), "Label from parameterless init");
        AssertEqual(0, obj.Value, "Value from parameterless init");
        AssertEqual(false, obj.Enabled, "Enabled from parameterless init");
        TestLogger.Info("MultiInitClass() parameterless constructor passed");
    }

    public void TestMultiInitClassBoolParam()
    {
        // Tests class constructor with bool parameter gets @_cdecl wrapper.
        // Previously crashed Mono JIT: BooleanDisposable(bool) pattern (MarshalAs + CallConvSwift).
        var obj = new MultiInitClass(enabled: true);
        AssertEqual("enabled", obj.Label.ToString(), "Label from bool init(true)");
        AssertEqual(1, obj.Value, "Value from bool init(true)");
        AssertEqual(true, obj.Enabled, "Enabled from bool init(true)");
        TestLogger.Info("MultiInitClass(enabled: true) constructor passed");
    }

    public void TestMultiInitClassBoolParamFalse()
    {
        var obj = new MultiInitClass(enabled: false);
        AssertEqual("disabled", obj.Label.ToString(), "Label from bool init(false)");
        AssertEqual(0, obj.Value, "Value from bool init(false)");
        AssertEqual(false, obj.Enabled, "Enabled from bool init(false)");
        TestLogger.Info("MultiInitClass(enabled: false) constructor passed");
    }

    public void TestMultiInitClassStringIntParam()
    {
        // String param constructor already had @_cdecl — verify it still works.
        var obj = new MultiInitClass(label: "custom", value: 42);
        AssertEqual("custom", obj.Label.ToString(), "Label from string+int init");
        AssertEqual(42, obj.Value, "Value from string+int init");
        AssertEqual(true, obj.Enabled, "Enabled from string+int init");
        TestLogger.Info("MultiInitClass(label:value:) constructor passed");
    }

    public void TestMultiInitClassDescribe()
    {
        var obj = new MultiInitClass(enabled: true);
        var desc = obj.GetDescribe();
        AssertTrue(desc.Contains("enabled"), "Describe contains label");
        AssertTrue(desc.Contains("1"), "Describe contains value");
        AssertTrue(desc.Contains("true"), "Describe contains enabled");
        TestLogger.Info($"MultiInitClass.Describe() = {desc}");
    }

    public void TestMultiInitClassFactoryFunction()
    {
        // Factory function tests that the parameterless constructor's @_cdecl wrapper
        // is correctly connected when called from Swift.
        var obj = TestLibFunctions.CreateMultiInitDefault();
        AssertEqual("default", obj.Label.ToString(), "Factory returns default label");
        AssertEqual(0, obj.Value, "Factory returns default value");
        TestLogger.Info("createMultiInitDefault() factory function passed");
    }

    #endregion

    #region FrozenRect — Large Frozen Struct Constructor @_cdecl Wrapper

    public void TestFrozenRectConstruction()
    {
        // Tests 32-byte frozen struct constructor gets @_cdecl wrapper.
        // Previously crashed Mono JIT: URLEncoding(dest,array,bool) pattern
        // (CallConvSwift + SwiftIndirectResult for >16 byte return).
        var rect = new FrozenRect(x: 10.0, y: 20.0, width: 100.0, height: 50.0);
        AssertApproxEqual(10.0, rect.X, message: "X field");
        AssertApproxEqual(20.0, rect.Y, message: "Y field");
        AssertApproxEqual(100.0, rect.Width, message: "Width field");
        AssertApproxEqual(50.0, rect.Height, message: "Height field");
        TestLogger.Info("FrozenRect(x:y:width:height:) 32-byte constructor passed");
    }

    public void TestFrozenRectArea()
    {
        var rect = new FrozenRect(x: 0.0, y: 0.0, width: 8.0, height: 5.0);
        AssertApproxEqual(40.0, rect.Area, message: "Area = width * height");
        TestLogger.Info("FrozenRect.Area computed property passed");
    }

    public void TestFrozenRectOffset()
    {
        var rect = new FrozenRect(x: 10.0, y: 20.0, width: 100.0, height: 50.0);
        var moved = rect.Offset(dx: 5.0, dy: -3.0);
        AssertApproxEqual(15.0, moved.X, message: "Offset X");
        AssertApproxEqual(17.0, moved.Y, message: "Offset Y");
        AssertApproxEqual(100.0, moved.Width, message: "Width unchanged");
        AssertApproxEqual(50.0, moved.Height, message: "Height unchanged");
        TestLogger.Info("FrozenRect.Offset(dx:dy:) method passed");
    }

    public void TestFrozenRectDescribe()
    {
        var rect = new FrozenRect(x: 1.5, y: 2.5, width: 3.0, height: 4.0);
        var desc = TestLibFunctions.DescribeFrozenRect(rect);
        AssertTrue(desc.Contains("1.5") || desc.Contains("1,5"), "Description contains X");
        AssertTrue(desc.Contains("2.5") || desc.Contains("2,5"), "Description contains Y");
        TestLogger.Info($"DescribeFrozenRect() = {desc}");
    }

    #endregion

    #region ArrayInitHolder — Non-Blittable Constructor @_cdecl Wrapper (BUG-3)

    public void TestArrayInitHolderSimpleConstructor()
    {
        // Simple constructor with blittable Int32 param — always works.
        var obj = new ArrayInitHolder(count: 42);
        AssertEqual(42, obj.Count, "Count from simple init");
        AssertEqual("count-only", obj.Label.ToString(), "Label from simple init");
        TestLogger.Info("ArrayInitHolder(count:) simple constructor passed");
    }

    public void TestArrayInitHolderArrayConstructor()
    {
        // Array<String> constructor — non-blittable, needs @_cdecl wrapper.
        // BUG-3 coverage: in BindingTests the wrapper is generated and uses CallConvCdecl.
        // Without the fix, third-party libs without wrapper support would emit a raw
        // CallConvSwift P/Invoke that crashes Mono JIT.
        var obj = new ArrayInitHolder(items: new[] { "hello", "world" });
        AssertEqual(2, obj.Count, "Count from array init");
        AssertTrue(obj.Label.ToString().Contains("hello"), "Label contains first item");
        AssertTrue(obj.Label.ToString().Contains("world"), "Label contains second item");
        TestLogger.Info("ArrayInitHolder(items:) array constructor passed");
    }

    public void TestArrayInitHolderDescribe()
    {
        var obj = new ArrayInitHolder(count: 7);
        var desc = obj.GetDescribe();
        AssertTrue(desc.Contains("7"), "Describe contains count");
        AssertTrue(desc.Contains("count-only"), "Describe contains label");
        TestLogger.Info($"ArrayInitHolder.GetDescribe() = {desc}");
    }

    #endregion

    #region FinalPropertyHolder — Final Class Instance Property @_cdecl Wrappers

    public void TestFinalPropertyHolderConstruction()
    {
        var obj = new FinalPropertyHolder(intValue: 42, floatValue: 3.14, stringValue: "hello", boolValue: true);
        AssertEqual(42, obj.IntValue, "IntValue getter");
        AssertApproxEqual(3.14, obj.FloatValue, message: "FloatValue getter");
        AssertEqual("hello", obj.StringValue.ToString(), "StringValue getter");
        AssertEqual(true, obj.BoolValue, "BoolValue getter");
        TestLogger.Info("FinalPropertyHolder construction + getter passed");
    }

    public void TestFinalPropertyHolderIntSetGet()
    {
        // Tests final class Int32 property getter/setter via @_cdecl wrapper.
        // Previously used CallConvSwift + SwiftSelf which crashed Mono JIT.
        var obj = new FinalPropertyHolder(intValue: 0, floatValue: 0.0, stringValue: "", boolValue: false);
        obj.IntValue = 99;
        AssertEqual(99, obj.IntValue, "IntValue after set");
        TestLogger.Info("FinalPropertyHolder.IntValue set/get passed");
    }

    public void TestFinalPropertyHolderFloatSetGet()
    {
        var obj = new FinalPropertyHolder(intValue: 0, floatValue: 0.0, stringValue: "", boolValue: false);
        obj.FloatValue = 2.718;
        AssertApproxEqual(2.718, obj.FloatValue, message: "FloatValue after set");
        TestLogger.Info("FinalPropertyHolder.FloatValue set/get passed");
    }

    public void TestFinalPropertyHolderStringSetGet()
    {
        var obj = new FinalPropertyHolder(intValue: 0, floatValue: 0.0, stringValue: "old", boolValue: false);
        obj.StringValue = new SwiftString("new");
        AssertEqual("new", obj.StringValue.ToString(), "StringValue after set");
        TestLogger.Info("FinalPropertyHolder.StringValue set/get passed");
    }

    public void TestFinalPropertyHolderBoolSetGet()
    {
        var obj = new FinalPropertyHolder(intValue: 0, floatValue: 0.0, stringValue: "", boolValue: false);
        obj.BoolValue = true;
        AssertEqual(true, obj.BoolValue, "BoolValue after set");
        obj.BoolValue = false;
        AssertEqual(false, obj.BoolValue, "BoolValue back to false");
        TestLogger.Info("FinalPropertyHolder.BoolValue set/get round-trip passed");
    }

    public void TestFinalPropertyHolderSummary()
    {
        var obj = new FinalPropertyHolder(intValue: 7, floatValue: 1.5, stringValue: "test", boolValue: true);
        var summary = obj.Summary.ToString();
        AssertTrue(summary.Contains("7"), "Summary contains int");
        AssertTrue(summary.Contains("test"), "Summary contains string");
        AssertTrue(summary.Contains("true"), "Summary contains bool");
        TestLogger.Info($"FinalPropertyHolder.Summary = {summary}");
    }

    #endregion

    #region sumSevenInts — Register-Spill Thunk Symmetry (@_cdecl Fallback)

    public void TestSumSevenIntsRegisterSpillFallback()
    {
        // Seven Int args fit arm64's eight integer argument registers but spill past x86_64 SysV's
        // six, so the x86_64 thunk declines. The generator must fall the whole method back to the
        // @_cdecl wrapper rather than emit an arm64-only thunk the architecture-neutral C# would then
        // import on x86_64 — that would throw EntryPointNotFound on the Rosetta slice. A correct sum
        // proves the wrapper symbol resolves and is wired on both architectures.
        var sum = TestLibFunctions.SumSevenInts(1, 2, 3, 4, 5, 6, 7);
        AssertEqual((nint)28, sum, "sumSevenInts(1..7) = 28");
        TestLogger.Info($"SumSevenInts(1..7) = {sum}");
    }

    #endregion
}
