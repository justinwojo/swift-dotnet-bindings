// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// The theme bridge of a <c>@MainActor</c> singleton. Its setter and getter bodies touch the
/// singleton inside the bridge's main-thread helper, which only compiles when that helper runs its
/// block on the main actor; at run time each call must reach the singleton's own property. The
/// runner calls these on the main thread, the helper's direct path.
/// </summary>
public class MainActorThemeBridgeTests : TestBase
{
    public MainActorThemeBridgeTests(TestResults results) : base(results) { }

    public void TestColorSetterReachesTheSingletonAndTheGetterReadsItBack()
    {
        var original = global::SwiftBindingsTestLib.MainActorBridgeTheme.GetAccent();
        AssertApproxEqual(0.1, original.R, 1e-3, "The getter read the singleton's initial red");
        AssertApproxEqual(0.3, original.B, 1e-3, "The getter read the singleton's initial blue");
        try
        {
            global::SwiftBindingsTestLib.MainActorBridgeTheme.SetAccent(new Swift.SwiftColor(0.25, 0.5, 0.75, 1.0));
            var read = global::SwiftBindingsTestLib.MainActorBridgeTheme.GetAccent();
            AssertApproxEqual(0.25, read.R, 1e-3, "red round-trips through the singleton");
            AssertApproxEqual(0.5, read.G, 1e-3, "green round-trips through the singleton");
            AssertApproxEqual(0.75, read.B, 1e-3, "blue round-trips through the singleton");
            AssertApproxEqual(1.0, read.A, 1e-3, "alpha round-trips through the singleton");
        }
        finally
        {
            global::SwiftBindingsTestLib.MainActorBridgeTheme.SetAccent(original);
        }
    }

    public void TestFontSetterReachesTheSingleton()
    {
        // Font has no getter in the bridge, so this is a smoke test: each call must complete on the
        // main actor without trapping in MainActor.assumeIsolated.
        global::SwiftBindingsTestLib.MainActorBridgeTheme.SetHeadline(Swift.SwiftFont.System(21));
        global::SwiftBindingsTestLib.MainActorBridgeTheme.SetHeadline(Swift.SwiftFont.Custom("Menlo", 13));
    }
}

#endif
