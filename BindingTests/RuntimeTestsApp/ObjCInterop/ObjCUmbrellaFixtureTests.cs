// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using ObjCUmbrella;
using RuntimeTestsApp.Infrastructure;
using UIKit;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// W-1 — end-to-end gate for the pure-ObjC clang-umbrella fixture (<c>Sources/ObjCUmbrella/</c>),
/// the first authored <c>module.modulemap</c> library in the harness and the durable regression
/// gate for the generator behaviors a real pure-ObjC framework (e.g. MapLibre) depends on.
///
/// The fixture is generated through the generator's <c>--objc</c> (bgen) pipeline and consumed here
/// via a single ProjectReference to the emitted binding project. Each test exercises one of the six
/// deliberately-shaped cases; the fact that ANY of these tests runs at all already proves the app
/// launched without an Objective-C duplicate-selector registration abort (Shape 1's failure mode).
///
/// Shape 7 additionally guards the standard-Apple-type registry (objc-type-mappings.json): four
/// members whose signatures reference <c>NSOperatingSystemVersion</c>, <c>NSDataReadingOptions</c>,
/// <c>NSURLSessionTaskState</c>, and <c>UIApplicationState</c> — types that were once absent from
/// the registry and so silently dropped every member using them. That the members below EXIST to be
/// called (this file compiles) is the standing proof the registry gap stays closed.
/// </summary>
public class ObjCUmbrellaFixtureTests : TestBase
{
    public ObjCUmbrellaFixtureTests(TestResults results) : base(results) { }

    /// <summary>
    /// Shape 1 — a selector exposed as BOTH a property and a method. The generator flattened the
    /// duplicate <c>tally</c> selector onto the property (dropping the colliding method), so the
    /// ObjC registrar sees one registration and the app launches. The property round-trips.
    /// </summary>
    public void TestDuplicateSelectorFlattensToProperty()
    {
        using var counter = new OUCounter();
        AssertEqual(7, (int)counter.Tally, "flattened `tally` property reads its backing value");
    }

    /// <summary>
    /// Shape 2 — a real exported C function binds via <c>DllImport("__Internal")</c>. Its
    /// <c>static inline</c> sibling (<c>OUInlineSquare</c>) was correctly excluded — were it bound,
    /// the P/Invoke would fail to link, so this file compiling + running proves the exclusion.
    /// </summary>
    public void TestExportedCFunctionBinds()
    {
        AssertEqual(12, ObjCUmbrellaConstants.OUExportedTriple(4), "exported C function round-trips");
    }

    /// <summary>
    /// Shape 3 — an ObjC protocol used as a collection element (<c>NSArray&lt;id&lt;OUElement&gt;&gt;</c>)
    /// projects to <c>IOUElement[]</c> (single <c>I</c>, no double-<c>I</c> collapse) and each element
    /// round-trips its protocol method through the marshalled array.
    /// </summary>
    public void TestProtocolTypedArrayRoundTrips()
    {
        using var container = new OUContainer();
        IOUElement[] elements = container.MakeElements();
        AssertNotNull(elements, "container returned a protocol-typed array");
        AssertEqual(2, elements.Length, "array carried both elements");
        AssertEqual("element:alpha", elements[0].DescribeElement(), "first element dispatches its protocol method");
        AssertEqual("element:beta", elements[1].DescribeElement(), "second element dispatches its protocol method");
    }

    /// <summary>
    /// Shape 4 — a delegate protocol with an OPTIONAL callback. A C# subclass installs itself as the
    /// notifier's listener; when Objective-C fires <c>emit:</c> the optional selector dispatches back
    /// into the managed override (ObjC→C# reverse dispatch).
    /// </summary>
    public void TestOptionalDelegateCallbackDispatchesToManaged()
    {
        using var notifier = new OUNotifier();
        var listener = new CapturingListener();
        notifier.Listener = listener;

        AssertFalse(listener.Received.HasValue, "listener has not been called yet");
        notifier.Emit(42);
        AssertTrue(listener.Received.HasValue, "the optional callback fired into managed code");
        AssertEqual(42, (int)listener.Received!.Value, "the callback carried the emitted value");
    }

    /// <summary>
    /// Shape 5 (ML-1 regression) — a bare <c>camera</c> property alongside a
    /// <c>camera:fittingX:edgePadding:</c> method whose first selector segment collides with the
    /// property's name. The property kept <c>Camera</c>; the method disambiguated to
    /// <c>CameraFittingXEdgePadding</c>. Both coexist as distinct C# members (the fact this compiles
    /// is the guard for commit 3e5a0a5e) and both round-trip.
    /// </summary>
    public void TestCameraPropertyAndMethodDisambiguate()
    {
        using var map = new OUMapView();

        // The disambiguated method: altitude = bounds + x + padding = 1 + 2 + 3 = 6.
        using var fitted = map.CameraFittingXEdgePadding(1, 2, 3);
        AssertEqual(6, (int)fitted.Altitude, "the disambiguated `camera:fittingX:edgePadding:` method round-trips");

        // The property that KEPT the bare `Camera` name, set and read back independently.
        using var assigned = new OUCamera(9);
        map.Camera = assigned;
        AssertEqual(9, (int)map.Camera.Altitude, "the `camera` property round-trips a distinct value");
    }

    /// <summary>
    /// Shape 6 — a block whose RETURN type is a protocol (<c>id&lt;OUElement&gt;</c>), the exact shape of
    /// Google AdMob's mediation <c>...LoadCompletionHandler</c>. The generator widened the protocol-typed
    /// block return to <c>NSObject</c> in that slot — a bare <c>IOUElement</c> there fails to compile
    /// (CS1503) inside bgen's generated block trampoline. The managed factory returns a conforming
    /// <c>OUElementBox</c>; Objective-C invokes the block, then dispatches the element's protocol method
    /// back to produce the round-tripped text.
    /// </summary>
    public void TestProtocolReturningBlockRoundTrips()
    {
        using var host = new OUFactoryHost();
        string described = host.RunFactory((nint index) => new OUElementBox($"factory-{index}"));
        AssertEqual("element:factory-3", described, "the protocol-returning factory block round-trips through NSObject");
    }

    /// <summary>
    /// Shape 7 (A2) — four members whose signatures reference standard Apple value types that were
    /// once absent from the ObjC type registry (<c>NSOperatingSystemVersion</c> struct;
    /// <c>NSDataReadingOptions</c>, <c>NSURLSessionTaskState</c>, <c>UIApplicationState</c> enums), so
    /// any member using them was silently dropped as an unresolvable type. Registering them closed the
    /// gap; each member below binding AND resolving its type against Microsoft.iOS is the durable proof.
    /// Note the <c>URL</c>→<c>Url</c> acronym on the projected <c>NSUrlSessionTaskState</c>.
    /// </summary>
    public void TestStandardAppleTypesResolveAndRoundTrip()
    {
        using var system = new OUSystemTypes();

        NSOperatingSystemVersion version = system.MinimumVersion();
        AssertEqual(15, (int)version.Major, "NSOperatingSystemVersion struct round-trips its major component");
        AssertEqual(2, (int)version.Minor, "NSOperatingSystemVersion struct round-trips its minor component");

        // Microsoft.iOS names the `NSDataReadingMappedIfSafe` bit `Mapped`.
        AssertTrue(system.AcceptsReadingOptions(NSDataReadingOptions.Mapped),
            "NSDataReadingOptions flag is honored when the mapped-if-safe bit is set");
        AssertFalse(system.AcceptsReadingOptions(NSDataReadingOptions.Uncached),
            "NSDataReadingOptions flag is rejected when a different bit is set");

        AssertEqual((int)NSUrlSessionTaskState.Suspended, (int)system.CurrentTaskState(),
            "NSUrlSessionTaskState enum round-trips");
        AssertEqual((int)UIApplicationState.Background, (int)system.PreferredApplicationState(),
            "UIApplicationState enum round-trips");

        // The FBSDKTypeUtility JSON surface: NSJSONReadingOptions / NSJSONWritingOptions, projected with
        // the JSON→Json acronym. MutableContainers / PrettyPrinted round-trip through the registered types.
        AssertEqual((int)NSJsonReadingOptions.MutableContainers, (int)system.DefaultReadingOptions(),
            "NSJsonReadingOptions enum round-trips");
        AssertEqual((int)NSJsonWritingOptions.PrettyPrinted, (int)system.DefaultWritingOptions(),
            "NSJsonWritingOptions enum round-trips");
    }

    /// <summary>
    /// Managed adopter of the optional-callback protocol. Conforming to <see cref="IOUListener"/> plus
    /// the <c>[Export("didReceiveValue:")]</c> selector makes <c>respondsToSelector:</c> return true,
    /// so the notifier invokes it.
    /// </summary>
    private sealed class CapturingListener : NSObject, IOUListener
    {
        public nint? Received { get; private set; }

        [Export("didReceiveValue:")]
        public void DidReceiveValue(nint value) => Received = value;
    }
}
