// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests the Lottie-style ValueProvider pattern:
/// - Protocol with required members + extension defaults
/// - Concrete types implementing the protocol interface at compile time
/// - SetValueProvider pattern: passing concrete types through protocol-typed parameters
/// - GradientProvider construction with array properties
///
/// Exercises the phantom defaults fix (L1) and GradientValueProvider coverage (L3).
/// </summary>
public class ValueProviderPatternTests : TestBase
{
    public ValueProviderPatternTests(TestResults results) : base(results) { }

    #region GradientProvider Construction

    public void TestGradientProviderConstruction()
    {
        using var colors = new Swift.SwiftArray<double>(new double[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 });
        using var locations = new Swift.SwiftArray<double>(new double[] { 0.0, 1.0 });
        using var provider = new GradientProvider(colors: colors, locations: locations);
        AssertEqual(2, provider.StopCount(), "GradientProvider stop count");
        TestLogger.Info($"GradientProvider created with {provider.StopCount()} stops");
    }

    public void TestGradientProviderValueKind()
    {
        using var colors = new Swift.SwiftArray<double>(new double[] { 1.0, 0.5, 0.0 });
        using var locations = new Swift.SwiftArray<double>(new double[] { 0.0, 0.5, 1.0 });
        using var provider = new GradientProvider(colors: colors, locations: locations);
        AssertEqual("gradient", provider.ValueKind, "GradientProvider.ValueKind");
        TestLogger.Info($"GradientProvider.ValueKind = \"{provider.ValueKind}\"");
    }

    public void TestGradientProviderHasUpdate()
    {
        using var colors = new Swift.SwiftArray<double>(new double[] { 1.0 });
        using var locations = new Swift.SwiftArray<double>(new double[] { 0.0 });
        using var provider = new GradientProvider(colors: colors, locations: locations);
        // GradientProvider uses the default hasUpdate which returns false
        AssertFalse(provider.HasUpdate(frame: 0.0), "GradientProvider.HasUpdate should be false (default)");
        TestLogger.Info("GradientProvider uses extension default hasUpdate=false");
    }

    #endregion

    #region FloatProvider and ColorProvider Construction

    public void TestFloatProviderConstruction()
    {
        using var provider = new FloatProvider(floatValue: 42.5);
        AssertEqual("float", provider.ValueKind, "FloatProvider.ValueKind");
        // FloatProvider overrides hasUpdate to return true
        AssertTrue(provider.HasUpdate(frame: 0.0), "FloatProvider.HasUpdate should be true (overridden)");
        TestLogger.Info($"FloatProvider(42.5): kind=\"{provider.ValueKind}\", hasUpdate=true");
    }

    public void TestColorProviderConstruction()
    {
        using var provider = new ColorProvider(r: 1.0, g: 0.5, b: 0.25, a: 1.0);
        AssertEqual("color", provider.ValueKind, "ColorProvider.ValueKind");
        // ColorProvider uses the default hasUpdate which returns false
        AssertFalse(provider.HasUpdate(frame: 0.0), "ColorProvider.HasUpdate should be false (default)");
        TestLogger.Info($"ColorProvider(1,0.5,0.25,1): kind=\"{provider.ValueKind}\"");
    }

    #endregion

    #region Protocol Interface Conformance (L1 phantom defaults fix)

    public void TestFloatProviderImplementsIValueProviding()
    {
        AssertTrue(typeof(IValueProviding).IsAssignableFrom(typeof(FloatProvider)),
            "FloatProvider implements IValueProviding");
        TestLogger.Info("FloatProvider : IValueProviding");
    }

    public void TestColorProviderImplementsIValueProviding()
    {
        AssertTrue(typeof(IValueProviding).IsAssignableFrom(typeof(ColorProvider)),
            "ColorProvider implements IValueProviding");
        TestLogger.Info("ColorProvider : IValueProviding");
    }

    public void TestGradientProviderImplementsIValueProviding()
    {
        AssertTrue(typeof(IValueProviding).IsAssignableFrom(typeof(GradientProvider)),
            "GradientProvider implements IValueProviding");
        TestLogger.Info("GradientProvider : IValueProviding");
    }

    public void TestValueKindThroughInterface()
    {
        using var provider = new FloatProvider(floatValue: 1.0);
        var iface = (IValueProviding)provider;
        AssertEqual("float", iface.ValueKind, "IValueProviding.ValueKind via cast");
        TestLogger.Info($"((IValueProviding)FloatProvider).ValueKind = \"{iface.ValueKind}\"");
    }

    #endregion

    #region SetValueProvider Pattern (AnimationContainer)

    public void TestSetProviderWithFloatProvider()
    {
        using var container = new AnimationContainer();
        using var provider = new FloatProvider(floatValue: 0.5);
        using var keypath = new AnimKeypath(keypath: "**.Opacity");
        container.SetProvider(provider, keypath: keypath);
        AssertEqual(1, container.GetProviderCount(), "Container has 1 provider");
        AssertEqual("float", container.ValueKindForKeypath("**.Opacity"), "Keypath resolves to float");
        TestLogger.Info("SetProvider(FloatProvider, '**.Opacity') succeeded");
    }

    public void TestSetProviderWithColorProvider()
    {
        using var container = new AnimationContainer();
        using var provider = new ColorProvider(r: 1.0, g: 0.0, b: 0.0, a: 1.0);
        using var keypath = new AnimKeypath(keypath: "**.Fill 1.Color");
        container.SetProvider(provider, keypath: keypath);
        AssertEqual("color", container.ValueKindForKeypath("**.Fill 1.Color"), "Keypath resolves to color");
        TestLogger.Info("SetProvider(ColorProvider, '**.Fill 1.Color') succeeded");
    }

    public void TestSetProviderWithGradientProvider()
    {
        using var container = new AnimationContainer();
        using var colors = new Swift.SwiftArray<double>(new double[] { 1.0, 0.0, 0.0, 0.0, 0.0, 1.0 });
        using var locations = new Swift.SwiftArray<double>(new double[] { 0.0, 1.0 });
        using var provider = new GradientProvider(colors: colors, locations: locations);
        using var keypath = new AnimKeypath(keypath: "**.Gradient Fill.Colors");
        container.SetProvider(provider, keypath: keypath);
        AssertEqual("gradient", container.ValueKindForKeypath("**.Gradient Fill.Colors"), "Keypath resolves to gradient");
        TestLogger.Info("SetProvider(GradientProvider, '**.Gradient Fill.Colors') succeeded");
    }

    public void TestMultipleProviders()
    {
        using var container = new AnimationContainer();
        using var fp = new FloatProvider(floatValue: 1.0);
        using var cp = new ColorProvider(r: 0.0, g: 1.0, b: 0.0, a: 1.0);
        using var gColors = new Swift.SwiftArray<double>(new double[] { 0.5, 0.5, 0.5 });
        using var gLocs = new Swift.SwiftArray<double>(new double[] { 0.0, 0.5, 1.0 });
        using var gp = new GradientProvider(colors: gColors, locations: gLocs);
        using var kp1 = new AnimKeypath(keypath: "**.Opacity");
        using var kp2 = new AnimKeypath(keypath: "**.Fill.Color");
        using var kp3 = new AnimKeypath(keypath: "**.Gradient");
        container.SetProvider(fp, keypath: kp1);
        container.SetProvider(cp, keypath: kp2);
        container.SetProvider(gp, keypath: kp3);
        AssertEqual(3, container.GetProviderCount(), "Container has 3 providers");
        AssertEqual("float", container.ValueKindForKeypath("**.Opacity"), "Float at opacity");
        AssertEqual("color", container.ValueKindForKeypath("**.Fill.Color"), "Color at fill");
        AssertEqual("gradient", container.ValueKindForKeypath("**.Gradient"), "Gradient at gradient");
        TestLogger.Info("3 providers registered and resolved correctly");
    }

    public void TestHasUpdateForKeypath()
    {
        using var container = new AnimationContainer();
        using var fp = new FloatProvider(floatValue: 1.0);
        using var cp = new ColorProvider(r: 1.0, g: 0.0, b: 0.0, a: 1.0);
        using var kp1 = new AnimKeypath(keypath: "float");
        using var kp2 = new AnimKeypath(keypath: "color");
        container.SetProvider(fp, keypath: kp1);
        container.SetProvider(cp, keypath: kp2);
        // FloatProvider overrides hasUpdate -> true; ColorProvider uses default -> false
        AssertTrue(container.HasUpdateForKeypath("float", frame: 0.0), "Float provider has update");
        AssertFalse(container.HasUpdateForKeypath("color", frame: 0.0), "Color provider default no update");
        TestLogger.Info("HasUpdateForKeypath dispatches correctly through protocol");
    }

    #endregion

    #region Free Functions with Existential Parameters

    public void TestGetProviderKindFreeFunction()
    {
        using var provider = new GradientProvider(
            colors: new Swift.SwiftArray<double>(new double[] { 1.0 }),
            locations: new Swift.SwiftArray<double>(new double[] { 0.0 }));
        var kind = Functions.GetProviderKind(provider);
        AssertEqual("gradient", kind, "getProviderKind() free function");
        TestLogger.Info($"getProviderKind(GradientProvider) = \"{kind}\"");
    }

    public void TestCheckProviderUpdateFreeFunction()
    {
        using var fp = new FloatProvider(floatValue: 1.0);
        using var cp = new ColorProvider(r: 0.0, g: 0.0, b: 0.0, a: 1.0);
        AssertTrue(Functions.CheckProviderUpdate(fp, frame: 30.0),
            "checkProviderUpdate(FloatProvider) should be true");
        AssertFalse(Functions.CheckProviderUpdate(cp, frame: 30.0),
            "checkProviderUpdate(ColorProvider) should be false");
        TestLogger.Info("checkProviderUpdate dispatches correctly");
    }

    #endregion
}
