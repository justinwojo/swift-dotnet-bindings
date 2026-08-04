// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using CoreGraphics;
using Foundation;
using ObjCRuntime;
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
/// via a single ProjectReference to the emitted binding project. Each test exercises one of the
/// deliberately-shaped cases; the fact that ANY of these tests runs at all already proves the app
/// launched without an Objective-C duplicate-selector registration abort (Shape 1's failure mode).
///
/// Shape 7 additionally guards the standard-Apple-type registry (objc-type-mappings.json): four
/// members whose signatures reference <c>NSOperatingSystemVersion</c>, <c>NSDataReadingOptions</c>,
/// <c>NSURLSessionTaskState</c>, and <c>UIApplicationState</c> — types that were once absent from
/// the registry and so silently dropped every member using them. That the members below EXIST to be
/// called (this file compiles) is the standing proof the registry gap stays closed.
///
/// Shape 8 covers the one rename defect a compiler cannot catch. When the emitter renames a C#
/// DECLARATION — the class/protocol clash suffix, or the .NET acronym convention — native identity is
/// preserved by <c>[BaseType(..., Name=)]</c> / <c>[Protocol(Name=)]</c>. Drop or misspell that and the
/// binding still compiles; the type simply registers under its managed spelling and the first message
/// send finds nothing. Every other rename defect (a reference left on the raw spelling, a dangling
/// <c>instancetype</c>) is a hard compile error already gated by the unit suite and the compile gate,
/// so these three tests are deliberately the whole runtime surface for renaming.
///
/// Shape 9 covers the two ways an Apple SDK type reaches the generator under a name it cannot resolve
/// on its own: a struct spelled through a typedef over a differently-named record tag
/// (<c>NSRange</c>/<c>_NSRange</c>), and an enum, which the parser's SDK provenance never sees because
/// that channel collects classes and protocols only. Both used to drop the whole member.
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
        AssertEqual(12, ObjCUmbrellaFunctions.OUExportedTriple(4), "exported C function round-trips");
    }

    /// <summary>
    /// Shape 13 — <c>extern</c> constants read their real native values.
    ///
    /// This is the one defect in the family a compiler cannot catch. A <c>[Field]</c> property
    /// declared in a bgen CORE SOURCE compiles to a get-only auto-property with no initializer:
    /// it binds, it IntelliSenses, and it returns null/zero forever. Only a declaration bgen reads
    /// out of <c>ApiDefinition.cs</c> gets the generated <c>Dlfcn</c> reader behind it. So the
    /// assertions below are value assertions, not null checks — every fixture value is unrelated to
    /// its symbol name, which also rules out a lookup that echoes the name back.
    /// </summary>
    public void TestExternConstantsReadNativeValues()
    {
        AssertEqual("ou.channel.default", ObjCUmbrellaConstants.DefaultChannelName?.ToString() ?? "<null>",
            "extern NSString constant reads its native value");
        AssertEqual(7, (int)ObjCUmbrellaConstants.MaxRetryCount, "extern NSInteger constant reads its native value");
        AssertEqual(2.5, ObjCUmbrellaConstants.ScaleFactor, "extern double constant reads its native value");
    }

    /// <summary>
    /// Shape 13b — a constant typed by a <c>typedef</c> OF <c>NSString *</c> (the
    /// NS_TYPED_EXTENSIBLE_ENUM idiom) binds as an <c>NSString</c> field. Resolving the typedef
    /// chain is what makes this reachable; treating <c>OUEventName</c> as an unknown type drops the
    /// constant entirely, so the member existing to be read is half the proof and the value is the
    /// other half.
    /// </summary>
    public void TestTypedefdNSStringConstantBinds()
    {
        AssertEqual("ou.event.launch", ObjCUmbrellaConstants.EventNameLaunch?.ToString() ?? "<null>",
            "typedef'd NSString constant reads its native value");
    }

    /// <summary>
    /// Shape 13d — a C <c>long</c> constant. <c>Dlfcn</c> reads word-sized integers only at native
    /// width, so the constant path promotes <c>long</c> to <c>nint</c>; without the promotion the
    /// constant has no reader and disappears from the binding. Its fixed-width sibling
    /// (<c>OUFixedWidthTicks</c>, an <c>int64_t</c>) is deliberately absent here — it must NOT be
    /// promoted, and drops with a recorded skip instead.
    /// </summary>
    public void TestNativeWidthLongConstantBinds()
    {
        AssertEqual(4096, (int)ObjCUmbrellaConstants.NativeWidthTicks, "extern long constant reads its native value");
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
    /// Shape 8a — the class/protocol clash suffix. <c>OUBadge</c> is both a class and a protocol; the
    /// protocol's managed interface was renamed <c>IOUBadgeProtocol</c> while
    /// <c>[Protocol(Name = "OUBadge")]</c> held its native registration in place. The rename is a
    /// declaration-side change only — the Objective-C runtime must still see the original name.
    /// </summary>
    public void TestClashRenamedProtocolKeepsNativeRegistration()
    {
        using var badge = new OUBadge("alpha");
        AssertEqual("badge:alpha", badge.BadgeLabel(), "the clash-renamed protocol's method dispatches through the class");

        // The class half kept the bare managed name and still satisfies the renamed interface.
        IOUBadgeProtocol asProtocol = badge;
        AssertEqual("badge:alpha", asProtocol.BadgeLabel(), "the class satisfies the renamed protocol interface");

        // The assertion with teeth: a MANAGED adopter of `IOUBadgeProtocol`, handed to Objective-C,
        // must register as conforming to the NATIVE protocol `OUBadge`. Only [Protocol(Name = ...)]
        // makes that true. (Asking the fixture's own OUBadge instance would prove nothing — it
        // declares the conformance in Objective-C source.)
        using var managed = new ManagedBadge();
        AssertTrue(OUBadge.AcceptsBadge(managed),
            "a managed adopter of the renamed interface conforms to the native protocol `OUBadge`");
    }

    /// <summary>
    /// Shape 8b — the .NET acronym convention renamed the DECLARATION <c>NSURLBadgeBox</c> →
    /// <c>NSUrlBadgeBox</c>. Two things must hold and neither is visible to a compiler: the class must
    /// still register natively as <c>NSURLBadgeBox</c> (a dropped <c>[BaseType(..., Name=)]</c> would
    /// register it under the managed spelling, and <c>objc_getClass</c> would miss), and both
    /// <c>instancetype</c> returners must hand back the renamed type.
    /// </summary>
    public void TestAcronymRenamedClassKeepsNativeRegistration()
    {
        var nativeClass = Class.GetHandle("NSURLBadgeBox");
        AssertTrue(nativeClass != NativeHandle.Zero, "the class is registered under its native ObjC name");

        using var box = NSUrlBadgeBox.DefaultBox();
        AssertNotNull(box, "the static `instancetype` returner produced an instance");
        AssertTrue(box.ClassHandle == nativeClass, "the renamed managed class binds the native `NSURLBadgeBox`");
        AssertEqual("default", box.Tag, "the static `instancetype` returner round-trips its value");

        using var rebox = box.ReboxWithTag("beta");
        AssertEqual("default+beta", rebox.Tag, "the instance `instancetype` returner round-trips through the renamed type");
    }

    /// <summary>
    /// Shape 8c — a renamed DELEGATE protocol behind the WeakDelegate pattern. <c>Delegate</c> is the
    /// <c>[Wrap]</c> half, typed by the <c>[Model]</c> class bgen generates from the renamed protocol
    /// declaration; assigning through it and receiving the callback proves the wrap was emitted against
    /// the renamed spelling AND that the model still registers as native <c>NSURLBadgeObserver</c>.
    /// </summary>
    public void TestRenamedDelegateProtocolDispatchesToManaged()
    {
        using var emitter = new NSUrlBadgeEmitter();
        var observer = new CapturingBadgeObserver();
        emitter.Delegate = observer;

        AssertNull(observer.Seen, "the observer has not been called yet");
        emitter.ChangeBadge("beta");
        AssertEqual("beta", observer.Seen, "the renamed delegate protocol dispatched back into managed code");

        // The callback firing is NOT by itself proof the protocol kept its native name — the member's
        // own [Export] is what answers respondsToSelector:. Ask Objective-C for the protocol directly.
        AssertTrue(emitter.DelegateConformsToObserverProtocol(),
            "the managed model registers as conforming to the native protocol `NSURLBadgeObserver`");
    }

    /// <summary>
    /// Shape 9a — a Foundation struct whose public name is a typedef over a differently-named record
    /// tag (<c>typedef struct _NSRange NSRange;</c>). Hopping that typedef rewrites the member's type
    /// to the private tag <c>_NSRange</c>, which no platform assembly declares, so the member fails the
    /// api-definition resolvability gate and vanishes. That these members exist to be called is the
    /// standing proof the public spelling is kept; the round-tripped values additionally assert the
    /// struct's field layout survived, in both return and parameter position.
    /// </summary>
    public void TestTypedefSpelledSystemStructRoundTrips()
    {
        using var ranges = new OURangeSugarTypes();

        NSRange range = ranges.RangeWithLocation(4, 6);
        AssertEqual(4, (int)range.Location, "the typedef-spelled struct round-trips its location field");
        AssertEqual(6, (int)range.Length, "the typedef-spelled struct round-trips its length field");

        AssertEqual(10, (int)ranges.EndOfRange(range), "the struct passes back into ObjC by value");
        AssertEqual(9, (int)ranges.CombinedLengthOfRange(new NSRange(0, 3), new NSRange(0, 6)),
            "two same-typed struct parameters both marshal");
    }

    /// <summary>
    /// Shape 9c — the same system struct in settable-PROPERTY position, which is where the Swift lane
    /// has its silent write-back trap: a Swift struct property hands back a wrapper CLASS over a copy,
    /// so <c>owner.Prop.Field = x</c> compiles, runs, and is discarded. The ObjC bridge projects a C
    /// struct onto the platform assembly's own value type instead, so the same spelling here —
    /// <c>ranges.Span.Location = 1;</c> — is a compile error (CS1612, "cannot modify the return value
    /// ... because it is not a variable"), never a silent no-op. The C# compiler is the guard, and
    /// there is no analyzer work for this lane. The value-type assertion below is what pins that
    /// difference: if the projection ever changed to a reference type, the trap would appear here too
    /// and this test would go red.
    /// </summary>
    public void TestSystemStructPropertyProjectsAsAValueType()
    {
        using var ranges = new OURangeSugarTypes();

        AssertTrue(typeof(NSRange).IsValueType,
            "the bridged system struct projects as a C# value type, so a field write through the " +
            "property getter cannot compile");

        ranges.Span = new NSRange(2, 5);
        AssertEqual(2, (int)ranges.Span.Location, "the struct property round-trips its location field");
        AssertEqual(5, (int)ranges.Span.Length, "the struct property round-trips its length field");

        // The write-back idiom — the fix SB1003 teaches for the Swift lane — is also the only
        // spelling the compiler allows here, and it reaches the owner.
        NSRange span = ranges.Span;
        span.Location = 11;
        ranges.Span = span;
        AssertEqual(11, (int)ranges.Span.Location, "write-back through the struct property reaches the owner");
    }

    /// <summary>
    /// Shape 9b — members typed by Apple SDK enums the platform assembly already declares. An SDK enum
    /// is absent from the parser's class/protocol provenance, so without the system-enum vocabulary
    /// these members have nothing to resolve against and are dropped. <c>NSComparisonResult</c>
    /// (Foundation) and <c>UIUserInterfaceStyle</c> (UIKit) come from two different frameworks, so both
    /// owning <c>using</c> directives are exercised; that they compare equal to the platform's own enum
    /// members proves the binding resolved to Microsoft.iOS's type, not a re-emitted local copy.
    /// </summary>
    public void TestSystemEnumMembersResolveAndRoundTrip()
    {
        using var enums = new OUSystemEnumTypes();

        AssertEqual((int)NSComparisonResult.Ascending, (int)enums.CompareLength(1, 2),
            "NSComparisonResult resolves to the Foundation enum and round-trips the ascending case");
        AssertEqual((int)NSComparisonResult.Same, (int)enums.CompareLength(2, 2),
            "NSComparisonResult round-trips the equal case");
        AssertEqual((int)NSComparisonResult.Descending, (int)enums.CompareLength(3, 2),
            "NSComparisonResult round-trips the descending case");

        AssertEqual((int)UIUserInterfaceStyle.Dark, (int)enums.PreferredInterfaceStyle(),
            "UIUserInterfaceStyle resolves to the UIKit enum in return position");
        AssertTrue(enums.AcceptsInterfaceStyle(UIUserInterfaceStyle.Dark),
            "UIUserInterfaceStyle marshals into ObjC in parameter position");
        AssertFalse(enums.AcceptsInterfaceStyle(UIUserInterfaceStyle.Light),
            "a different UIUserInterfaceStyle member is distinguished across the boundary");
    }

    /// <summary>
    /// Shape 12a — a C array of value types passed as an element pointer + <c>count:</c> pair, on a
    /// class factory. The values themselves are the assertion: projected as a C# <c>out</c>, the
    /// pointer parameter would zero the caller's storage before the call and every coordinate would
    /// arrive as (0,0) — which compiles, links, and runs, so only reading the data back catches it.
    /// </summary>
    public void TestConstPointArrayInputReachesNativeIntact()
    {
        var points = new CGPoint[] { new(1, 10), new(2, 20), new(4, 40) };
        using var buffer = OUPointBuffer.BufferWithPoints(points);

        AssertEqual(3, (int)buffer.StoredCount, "every element of the array crossed, not just the first");
        AssertApproxEqual(7.0, (double)buffer.SumOfX(), 0.001, "the element VALUES crossed intact (zeroes would sum to 0)");
    }

    /// <summary>
    /// Shape 12b — the same pair on an instance method, with a trailing value-type parameter that has
    /// to pass through the generated array overload untouched.
    /// </summary>
    public void TestConstPointArrayInputForwardsTrailingParameter()
    {
        using var buffer = OUPointBuffer.BufferWithPoints(new CGPoint[] { new(1, 1) });
        buffer.AppendPoints(new CGPoint[] { new(2, 2), new(3, 3) }, (nfloat)10);

        AssertEqual(3, (int)buffer.StoredCount, "the appended elements crossed");
        // 1 + (2*10) + (3*10): a dropped or zeroed scale would give 1 + 2 + 3.
        AssertApproxEqual(51.0, (double)buffer.SumOfX(), 0.001, "the trailing scale parameter passed through unchanged");
    }

    /// <summary>
    /// Shape 12c — a MUTABLE element pointer + <c>count:</c> is an OUTPUT buffer of <c>count</c>
    /// elements. As an <c>out</c> the callee would be handed room for exactly one element and write
    /// <c>count</c> of them, corrupting whatever followed; as an array it fills the caller's storage.
    /// </summary>
    public void TestMutablePointArrayOutputFillsCallerBuffer()
    {
        using var buffer = OUPointBuffer.BufferWithPoints(new CGPoint[] { new(5, 50), new(6, 60) });

        var destination = new CGPoint[2];
        buffer.CopyPointsInto(destination);

        AssertApproxEqual(5.0, (double)destination[0].X, 0.001, "the callee wrote the first element");
        AssertApproxEqual(60.0, (double)destination[1].Y, 0.001, "the callee wrote past the first element too");
    }

    /// <summary>
    /// Shape 12e — the positive control for the projection that stays. A single MUTABLE element
    /// pointer with no count really is one caller-allocated slot, so it remains an <c>out</c>
    /// parameter; the const-pointer work must not have swept it up.
    /// </summary>
    public void TestSingleMutablePointerRemainsOutParameter()
    {
        using var empty = OUPointBuffer.BufferWithPoints(System.Array.Empty<CGPoint>());
        AssertFalse(empty.TryFirstPoint(out _), "an empty buffer reports no first point");

        using var buffer = OUPointBuffer.BufferWithPoints(new CGPoint[] { new(9, 90) });
        AssertTrue(buffer.TryFirstPoint(out var first), "a populated buffer reports a first point");
        AssertApproxEqual(9.0, (double)first.X, 0.001, "the out parameter carries the value back");
    }

    /// <summary>
    /// Shape 11 — the inherited read-write property stays WRITABLE through a subclass-typed
    /// variable. Two things conspire to narrow it: the subclass's own read-only re-declaration
    /// (dropped in favour of the inherited member), and bgen inlining the read-only view from the
    /// protocol in the subclass's conformance list. That this method compiles at all is the
    /// assertion — a getter-only <c>Title</c> on the subclass is CS0200 — and the round-trip through
    /// the base-typed reference proves the re-declared accessors reach the same native property
    /// rather than some shadow copy.
    /// </summary>
    public void TestInheritedSetterReachableThroughRedeclaringSubclass()
    {
        using var shape = new OUPolyShape();
        shape.Title = "polyline";

        AssertEqual("polyline", shape.Title, "the setter is reachable through the subclass-typed variable");
        OUShape asBase = shape;
        AssertEqual("polyline", asBase.Title, "the write landed on the inherited property, not a shadow");
    }

    /// <summary>
    /// Shape 11 — the same guarantee for a subclass that only ADOPTS the narrowing protocol and
    /// re-declares nothing. The conformance alone is what makes bgen inline the read-only view, so
    /// dropping a subclass re-declaration cannot be the whole fix.
    /// </summary>
    public void TestInheritedSetterReachableThroughConformanceOnlySubclass()
    {
        using var shape = new OUQuietPolyShape();
        shape.Title = "quiet";

        AssertEqual("quiet", shape.Title, "the setter is reachable through the conformance-only subclass");
        AssertEqual("quiet", ((OUShape)shape).Title, "the write landed on the inherited property");
    }

    /// <summary>
    /// Shape 11 — the widening half. <c>rank</c> is read-only on the base and read-write on the
    /// subclass, so the subclass member genuinely adds surface and emits over the inherited one;
    /// it must still round-trip.
    /// </summary>
    public void TestWidenedSubclassPropertyRoundTrips()
    {
        using var baseShape = new OUShape();
        AssertEqual(1, (int)baseShape.Rank, "the base member reports its own value");

        using var shape = new OUPolyShape();
        shape.Rank = 42;
        AssertEqual(42, (int)shape.Rank, "the widened subclass member round-trips through its setter");
    }

    /// <summary>
    /// Shape 10 — a category on a FOREIGN class. <c>NSValue</c> belongs to Foundation, so the
    /// members cannot be folded into the base type's own binding; they land in the static extension
    /// class bgen compiles a <c>[Category]</c> interface into. bgen prepends a receiver to EVERY
    /// member of that class, <c>[Static]</c> included, so the class method's generated overload asks
    /// for an <c>NSValue</c> that a class method never sends to. Reaching it through the
    /// receiver-free overload is the assertion — the factory is callable without first conjuring an
    /// instance of the very type it exists to produce — and the round-trip proves the overload
    /// dispatches to the same selector rather than merely compiling.
    /// </summary>
    public void TestCategoryClassMethodCallableWithoutReceiver()
    {
        using var boxed = NSValue_OUBoxing.Ou_valueWithSpan(12.5);

        AssertApproxEqual(12.5, boxed.GetOu_spanValue(), 0.001, "the receiver-free factory produced a value carrying the span it was given");
    }

    /// <summary>
    /// Shape 10 — the instance half of the same category. A static extension class cannot hold an
    /// instance property (CS0708), so both properties are projected to accessor METHODS on the
    /// property's own selectors. The read-write one carries the load: the projection's accessor
    /// exports now also state the property's declared memory semantic, and a write that still lands
    /// on <c>setOu_spanRank:</c> is what shows that declaring it left dispatch alone.
    /// </summary>
    public void TestCategoryInstancePropertyAccessorsRoundTrip()
    {
        using var boxed = NSValue_OUBoxing.Ou_valueWithSpan(3.25);

        AssertEqual(0, (int)boxed.GetOu_spanRank(), "the read-write accessor reports the unset default");

        boxed.SetOu_spanRank(7);

        AssertEqual(7, (int)boxed.GetOu_spanRank(), "the projected setter stored through the property's own selector");
        AssertApproxEqual(3.25, boxed.GetOu_spanValue(), 0.001, "writing the read-write property left the readonly one alone");
    }

    /// <summary>
    /// Shape 14 — an enum whose cases share a prefix that is NOT the enum's own type name
    /// (<c>OUMapTiler</c> on <c>OUSourceKind</c>). The strip falls back to the module's registered
    /// tag, so the cases read <c>MapTiler</c>/<c>MapLibre</c>/<c>Mapbox</c> rather than repeating it.
    /// Naming is only half the claim: the stripped members must still carry the NATIVE values, which
    /// is what round-tripping them through Objective-C proves.
    /// </summary>
    public void TestModuleTagStrippedEnumCasesCarryNativeValues()
    {
        using var picker = new OUSourcePicker();

        AssertEqual((int)OUSourceKind.MapLibre, (int)picker.NextAfter(OUSourceKind.MapTiler), "the first case advances to the second");
        AssertEqual((int)OUSourceKind.Mapbox, (int)picker.NextAfter(OUSourceKind.MapLibre), "the second case advances to the third");
        AssertEqual((int)OUSourceKind.MapTiler, (int)picker.NextAfter(OUSourceKind.Mapbox), "the last case wraps to the first");
        AssertEqual(0, (int)OUSourceKind.MapTiler, "the stripped member kept its native raw value");
    }

    /// <summary>
    /// Shape 15 — a delegate protocol whose FIRST selector segment carries semantics
    /// (<c>chartViewDidFailRenderingChart:withReason:</c>). Only the receiver token is dropped, so the
    /// callback is <c>DidFailRenderingChartWithReason</c> instead of the bare <c>WithReason</c> the
    /// drop-part[0] rule used to produce. The sibling <c>chartView:didSelectIndex:</c> — where part[0]
    /// IS just the receiver — keeps today's name, which is why both are asserted together.
    /// </summary>
    public void TestDelegateSelectorKeepsSemanticsCarriedInFirstSegment()
    {
        using var chart = new OUChartView();
        var observer = new CapturingChartDelegate();
        chart.Delegate = observer;

        chart.FailWithReason("tiles");
        AssertEqual("tiles", observer.Reason, "the semantics-carrying callback dispatched into managed code");

        chart.SelectIndex(3);
        AssertEqual(3, (int)observer.SelectedIndex, "the receiver-only sibling callback still dispatches");
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

    /// <summary>
    /// Managed adopter of the renamed observer protocol, subclassing the generated <c>[Model]</c> class
    /// (Shape 8c). Overriding the optional callback is what the emitter's renamed <c>[Wrap]</c> property
    /// has to accept.
    /// </summary>
    private sealed class CapturingBadgeObserver : NSUrlBadgeObserver
    {
        public string? Seen { get; private set; }

        public override void BadgeDidChange(string tag) => Seen = tag;
    }

    /// <summary>
    /// Managed adopter of the clash-renamed protocol interface (Shape 8a). Handing this to Objective-C
    /// is what proves the renamed declaration still registers its conformance under the native name —
    /// the fixture's own <c>OUBadge</c> class cannot answer that, since it declares the conformance in
    /// Objective-C source regardless of what the binding says.
    /// </summary>
    private sealed class ManagedBadge : NSObject, IOUBadgeProtocol
    {
        [Export("badgeLabel")]
        public string BadgeLabel() => "badge:managed";
    }

    /// <summary>
    /// Managed adopter of the chart delegate (Shape 15), subclassing the generated <c>[Model]</c>
    /// class. Overriding by the renamed method name is the whole point: if the emitter dropped the
    /// semantics-carrying first segment, these overrides would name a member that does not exist.
    /// </summary>
    private sealed class CapturingChartDelegate : OUChartViewDelegate
    {
        public string? Reason { get; private set; }
        public nint SelectedIndex { get; private set; } = -1;

        public override void DidFailRenderingChartWithReason(OUChartView chartView, string reason) => Reason = reason;

        public override void DidSelectIndex(OUChartView chartView, nint index) => SelectedIndex = index;
    }
}
