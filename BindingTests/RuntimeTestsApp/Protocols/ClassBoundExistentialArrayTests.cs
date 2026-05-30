// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
using Swift;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests for class-bound (superclass-constrained) protocol existential ARRAYS —
/// `func installRecognizers() -> [any BoundRecognizer]` where `BoundRecognizer`
/// is constrained to a superclass (`GestureHostBase`). Reproduces the RealityKit
/// `ARView.installGestures(...) -> [any EntityGestureRecognizer]` crash.
///
/// A class-bound existential array element is a compact 2-word `[classRef][witnessTable]`
/// (16-byte stride), NOT the 5-word opaque `ExistentialContainerN` (40-byte stride).
/// Before the fix the array was marshalled as `SwiftArray&lt;ExistentialContainer1&gt;`,
/// so `SwiftArray.Get` over-read each element (base + i*40 against a 16-byte array) and
/// SIGSEGV'd on the first index. `Count` succeeded (header-only read).
///
/// The conformer also exposes an optional-class property (`hostEntity: GestureHostBase?`)
/// mirroring `EntityGestureRecognizer.entity: Entity?`, so these tests also exercise
/// dispatching a member on a Swift-backed class-bound proxy materialised from the array.
/// </summary>
public class ClassBoundExistentialArrayTests : TestBase
{
    public ClassBoundExistentialArrayTests(TestResults results) : base(results) { }

    #region Count (header-only — passed even pre-fix)

    public void TestInstallRecognizersCount()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();
        AssertNotNull(recognizers, "InstallRecognizers() returned non-null list");
        AssertEqual(2, recognizers.Count, "InstallRecognizers().Count");
        TestLogger.Info($"InstallRecognizers().Count = {recognizers.Count}");
    }

    #endregion

    #region Indexing the class-bound existential array (the crash site)

    public void TestInstallRecognizersIndexDoesNotCrash()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();
        // The over-read SIGSEGV happened on the FIRST index of a class-bound existential array.
        var first = recognizers[0];
        AssertNotNull(first, "recognizers[0] materialised a proxy without crashing");
        var second = recognizers[1];
        AssertNotNull(second, "recognizers[1] materialised a proxy without crashing");
        TestLogger.Info("Indexed both class-bound existential array elements without SIGSEGV");
    }

    public void TestInstallRecognizersEnumerationLabels()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();
        var labels = new List<string>();
        foreach (var r in recognizers)
            labels.Add(r.RecognizerLabel);
        AssertEqual(2, labels.Count, "enumerated label count");
        AssertEqual("pan", labels[0], "recognizers[0].RecognizerLabel");
        AssertEqual("tap", labels[1], "recognizers[1].RecognizerLabel");
        TestLogger.Info($"Class-bound proxy labels = [{string.Join(", ", labels)}]");
    }

    #endregion

    #region Optional-class property dispatch on the Swift-backed class-bound proxy (.entity analog)

    public void TestInstallRecognizersHostEntityRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();

        // First recognizer carries a non-nil host (tag 7); second carries nil.
        var firstHost = recognizers[0].HostEntity;
        AssertNotNull(firstHost, "recognizers[0].HostEntity non-null (Entity? analog)");
        AssertEqual(7, (int)firstHost!.HostTag, "recognizers[0].HostEntity.HostTag");

        var secondHost = recognizers[1].HostEntity;
        AssertNull(secondHost, "recognizers[1].HostEntity is null");
        TestLogger.Info("Optional-class property dispatched on class-bound proxy without crash");
    }

    public void TestInstallRecognizersCollidableExistentialRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();

        // `.Collidable` returns `(any BoundCollidable)?` — the `.Entity: (any HasCollision)?`
        // analog. Reading it must materialise a read-only proxy of a superclass-constrained
        // protocol off the Swift existential container, not throw "proxy not available".
        var firstCollidable = recognizers[0].Collidable;
        AssertNotNull(firstCollidable, "recognizers[0].Collidable non-null existential proxy");
        AssertEqual("collide", firstCollidable!.CollisionLabel, "recognizers[0].Collidable.CollisionLabel");

        var secondCollidable = recognizers[1].Collidable;
        AssertNull(secondCollidable, "recognizers[1].Collidable is null");
        TestLogger.Info("Class-bound existential-return getter materialised a read-only proxy");
    }

    #endregion

    #region Property-return variant of the class-bound existential array

    public void TestRecognizersPropertyEnumeration()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.Recognizers;
        AssertEqual(2, recognizers.Count, "Recognizers property .Count");
        var label0 = recognizers[0].RecognizerLabel;
        AssertEqual("pan", label0, "Recognizers[0].RecognizerLabel");
        TestLogger.Info("Class-bound existential array property enumerated without crash");
    }

    #endregion

    #region Class-bound existential METHOD return (EmitExistentialReturnMethodBody — scalar sibling of .Collidable)

    public void TestMakeCollidableMethodReturnRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();

        // `MakeCollidable()` is a witness-dispatched METHOD returning `any BoundCollidable`
        // (class-bound). The returned heap cell is a 2-word `[classRef][witnessTable]` (16 bytes);
        // reading it as a 5-word ExistentialContainer1 (40 bytes) over-reads 24 bytes past the
        // allocation. The read width must follow class-boundedness, same as the `.Collidable` getter.
        var c0 = recognizers[0].MakeCollidable();
        AssertNotNull(c0, "recognizers[0].MakeCollidable() materialised a proxy");
        AssertEqual("current-pan", c0.CollisionLabel, "recognizers[0].MakeCollidable().CollisionLabel");

        var c1 = recognizers[1].MakeCollidable();
        AssertEqual("current-tap", c1.CollisionLabel, "recognizers[1].MakeCollidable().CollisionLabel");
        TestLogger.Info("Class-bound existential method-return materialised a read-only proxy without over-read");
    }

    #endregion

    #region Class-bound existential read through ASYNC return (AsyncHarnessEmitter / WrapperEmitter.Async)

    public async Task TestFetchCollidableAsyncReturnRoundTrip()
    {
        var vendor = new CollidableAsyncVendor();

        // `fetchCollidable(label:) async -> any BoundCollidable` drives the async-harness existential
        // return. The completion callback reads the returned heap cell; a class-bound `any P` cell is a
        // 2-word [classRef][witnessTable] (16 bytes), so reading the 5-word ExistentialContainer1
        // (40 bytes) over-reads 24 bytes past the allocation.
        var collidable = await WithTimeout(
            vendor.FetchCollidableAsync("alpha"),
            DefaultAsyncTimeout);
        AssertNotNull(collidable, "FetchCollidableAsync materialised a proxy");
        AssertEqual("async-alpha", collidable.CollisionLabel, "FetchCollidableAsync(...).CollisionLabel");
        TestLogger.Info("Class-bound existential async-return materialised a proxy without over-read");
    }

    #endregion

    #region Class-bound existential read through CLOSURE parameter (ClosureEmitter cdecl reconstruction)

    public void TestWithCollidableClosureParamRoundTrip()
    {
        var vendor = new CollidableClosureVendor();

        // `withCollidable(label:_:)` invokes a C# closure with a class-bound `any BoundCollidable`.
        // The @convention(c) callback reconstructs the existential from the heap cell — a 2-word
        // class-bound layout, not the 5-word opaque container. The closure reads a member off the
        // materialised proxy and returns it, round-tripping the value back through Swift.
        var label = vendor.WithCollidable("beta", c => c.CollisionLabel);
        AssertEqual("closure-beta", label, "WithCollidable closure observed the class-bound proxy's CollisionLabel");
        TestLogger.Info("Class-bound existential closure-param reconstructed a proxy without over-read");
    }

    #endregion

    #region Class-bound existential read through ENUM payload (EnumHandler extraction)

    public void TestCollidableBoxEnumPayloadRoundTrip()
    {
        var vendor = new CollidableBoxVendor();

        // `CollidableBox.present(any BoundCollidable)` carries a class-bound existential payload.
        // Extracting it (TryGetPresent) reads the 2-word class-bound cell off the enum-copy buffer;
        // reading the 5-word container over-reads past the enum's payload region.
        var present = vendor.MakeBox(true, "gamma");
        AssertEqual(CollidableBox.CaseTag.Present, present.Tag, "MakeBox(true).Tag");
        AssertTrue(present.TryGetPresent(out var collidable), "TryGetPresent succeeded for .present");
        AssertEqual("box-gamma", collidable!.CollisionLabel, "CollidableBox.present payload CollisionLabel");

        var absent = vendor.MakeBox(false, "gamma");
        AssertEqual(CollidableBox.CaseTag.Absent, absent.Tag, "MakeBox(false).Tag");
        AssertFalse(absent.TryGetPresent(out _), "TryGetPresent returns false for .absent");
        TestLogger.Info("Class-bound existential enum payload extracted a proxy without over-read");
    }

    public void TestMakeCollidableIfOptionalMethodReturnRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();
        var recognizers = vendor.GetInstallRecognizers();

        // Optional class-bound existential method return — covers the isOptionalReturn branch
        // (resultPtr == nil → null; non-null → 2-word read + proxy).
        var present = recognizers[0].MakeCollidableIf(true);
        AssertNotNull(present, "MakeCollidableIf(true) non-null");
        AssertEqual("opt-pan", present!.CollisionLabel, "MakeCollidableIf(true).CollisionLabel");

        var absent = recognizers[0].MakeCollidableIf(false);
        AssertNull(absent, "MakeCollidableIf(false) is null");
        TestLogger.Info("Optional class-bound existential method-return round-tripped (present + nil)");
    }

    #endregion

    #region Concrete-class @_cdecl existential return (WrapperEmitter.Return / ExistentialBypass read-width)

    public void TestCurrentCollidableConcreteReturnRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();

        // `currentCollidable()` on a CONCRETE (non-protocol) class routes through the `@_cdecl`
        // wrapper return path (ExistentialBypass / WrapperEmitter.Return), NOT witness dispatch.
        // The class-bound cell must be read at its 2-word [classRef][witnessTable] width; reading
        // the 5-word opaque container pulls 24 bytes of uninitialized buffer into unused fields.
        var c = vendor.GetCurrentCollidable();
        AssertNotNull(c, "currentCollidable() materialised a proxy");
        AssertEqual("vendor-current", c.CollisionLabel, "currentCollidable().CollisionLabel");
        TestLogger.Info("Concrete-class class-bound existential return read at 2-word width");
    }

    public void TestCurrentCollidableIfConcreteOptionalReturnRoundTrip()
    {
        var vendor = new BoundRecognizerVendor();

        // Optional concrete-class return — `(any BoundCollidable)?`. This routes through
        // OptionalProjection's indirect-result existential path. A class-bound Optional<any P>
        // is a 2-word cell whose None is the null classRef at offset 0; reading it as the 5-word
        // opaque container with the offset-24 metadata-slot None check mis-reports a present value
        // as null (the sret buffer is zero-filled and Swift writes only 2 words).
        var present = vendor.CurrentCollidableIf(true);
        AssertNotNull(present, "CurrentCollidableIf(true) non-null");
        AssertEqual("vendor-opt", present!.CollisionLabel, "CurrentCollidableIf(true).CollisionLabel");

        var absent = vendor.CurrentCollidableIf(false);
        AssertNull(absent, "CurrentCollidableIf(false) is null");
        TestLogger.Info("Optional concrete-class class-bound existential return round-tripped (present + nil)");
    }

    public void TestOptionalClassBoundReturnReleasesExactlyOnce()
    {
        // Surviving-owner double-release probe for the Optional<any P_classbound> RETURN.
        // The vendor holds the SOLE strong ref and hands the SAME instance back, so C# adopts
        // a shared +1. Disposing the proxy runs ReleaseAdoptedSwiftContainer → one
        // Arc.UnknownObjectRelease. The generated `finally` also calls DestroyWireBufferRetains
        // on the oversized SwiftOptional<ExistentialContainer1> carrier, but that is a NO-OP for
        // the compact 2-word [classRef][witnessTable] layout: the opaque-Optional value witness
        // keys `.none` off the metadata word at offset 24, which stays zero in the AllocZeroed
        // buffer (Swift wrote only the 16-byte cell). If it were instead a real release, the
        // shared +1 would be over-released here and the post-dispose read through the vendor
        // would crash (UAF) or read garbage. A correct single release leaves the vendor's +1
        // intact and the label readable.
        var vendor = new RetainingCollidableVendor("shared-99");

        var proxy = vendor.BorrowCollidableIf(true);
        AssertNotNull(proxy, "BorrowCollidableIf(true) non-null");
        AssertEqual("shared-99", proxy!.CollisionLabel, "borrowed label before dispose");

        (proxy as IDisposable)?.Dispose();

        // The vendor still owns the instance; reading it AFTER the proxy released proves the
        // proxy released exactly once (no double-release of the shared payload).
        AssertEqual("shared-99", vendor.GetRetainedLabel(),
            "vendor instance survived proxy dispose (no double-release)");
        TestLogger.Info("Optional class-bound existential return released exactly once (surviving-owner probe)");
    }

    #endregion

    #region Objective-C-rooted class-bound existential (swift_unknownObjectRetain/Release)

    public void TestObjCBoundConcreteReturnRoundTrip()
    {
        // The conformer is NSObject-derived, so `any ObjCBoundCollidable` carries an Objective-C
        // object. Adoption takes a +1 via Arc.UnknownObjectRetain (dispatches to objc_retain),
        // not the native-only swift_retain — which would corrupt the ObjC refcount.
        var vendor = new ObjCBoundVendor();

        var c = vendor.GetCurrentCollidable();
        AssertNotNull(c, "ObjC-rooted currentCollidable() materialised a proxy");
        AssertEqual("objc-current", c.CollisionLabel, "ObjC currentCollidable().CollisionLabel");

        var present = vendor.CurrentCollidableIf(true);
        AssertNotNull(present, "ObjC CurrentCollidableIf(true) non-null");
        AssertEqual("objc-opt", present!.CollisionLabel, "ObjC CurrentCollidableIf(true).CollisionLabel");

        var absent = vendor.CurrentCollidableIf(false);
        AssertNull(absent, "ObjC CurrentCollidableIf(false) is null");
        TestLogger.Info("ObjC-rooted class-bound existential return round-tripped (scalar + optional)");
    }

    public void TestObjCBoundReturnReleasesExactlyOnce()
    {
        // Surviving-owner probe for the ObjC payload: disposing the proxy must release the
        // NSObject conformer through swift_unknownObjectRelease (→ objc_release) exactly once.
        // A native swift_release on an ObjC object would mis-handle the refcount and the
        // post-dispose read through the vendor would crash.
        var vendor = new ObjCBoundVendor();

        var proxy = vendor.BorrowCollidableIf(true);
        AssertNotNull(proxy, "ObjC BorrowCollidableIf(true) non-null");
        AssertEqual("objc-shared", proxy!.CollisionLabel, "ObjC borrowed label before dispose");

        (proxy as IDisposable)?.Dispose();

        AssertEqual("objc-shared", vendor.GetRetainedLabel(),
            "ObjC vendor instance survived proxy dispose (unknownObjectRelease balanced)");
        TestLogger.Info("ObjC-rooted class-bound existential released exactly once via unknownObjectRelease");
    }

    #endregion

    #region Closure-param existential return (ClosureEmitter.SwiftWrapper metatype .self)

    public void TestCollidableFromClosureParamReturnRoundTrip()
    {
        var vendor = new CollidableClosureVendor();

        // `collidableFrom(label:_:)` carries a closure PARAMETER and returns a single-protocol
        // `any BoundCollidable`. The closure-bearing wrapper renders the single-protocol metatype
        // bare (`BoundCollidable.self`), but the wrapper symbol must still exist — a stripped
        // wrapper surfaces as EntryPointNotFoundException here. The closure result folds into the
        // returned collisionLabel, so a correct round-trip proves the closure fired and the
        // class-bound existential read at 2-word width.
        var c = vendor.CollidableFrom("beta", x => x + 5);
        AssertNotNull(c, "CollidableFrom materialised a proxy");
        AssertEqual("from-beta-35", c.CollisionLabel, "CollidableFrom('beta', +5).CollisionLabel");
        TestLogger.Info("Closure-param single-protocol existential return round-tripped");
    }

    public void TestCollidableFromThrowingClosureParamReturnRoundTrip()
    {
        var vendor = new CollidableClosureVendor();

        // Throwing variant — covers the `methodDecl.Throws` branch of the closure-bearing wrapper.
        var c = vendor.CollidableFromThrowing("gamma", x => x + 2);
        AssertNotNull(c, "CollidableFromThrowing materialised a proxy");
        AssertEqual("throw-gamma-42", c.CollisionLabel, "CollidableFromThrowing('gamma', +2).CollisionLabel");
        TestLogger.Info("Throwing closure-param single-protocol existential return round-tripped");
    }

    public void TestCompositionFromClosureParamCompositionReturnRoundTrip()
    {
        var vendor = new CollidableClosureVendor();

        // `compositionFrom(name:_:)` returns a protocol COMPOSITION `any Nameable & Ageable`, which
        // the closure-bearing wrapper renders WITH the `any` keyword and so must parenthesize as
        // `(any Nameable & Ageable).self`. Emitting the bare `any … .self` parses as `any (… .self)`,
        // fails Swift compilation, and silently strips the wrapper symbol → EntryPointNotFoundException
        // at this call. A non-null return proves the wrapper symbol resolved — this composition case is
        // the only fixture that exercises that parenthesization branch.
        var person = vendor.CompositionFrom("Ada", x => x + 1);
        AssertNotNull(person, "CompositionFrom resolved the (any A & B).self wrapper symbol and materialised a proxy");

        // Dispatching a member on a Swift-backed protocol-COMPOSITION existential proxy is a separate,
        // by-design limitation: the generator emits composition-proxy accessors that throw because there
        // is no single witness table to route `.Name`/`.Age` through (ModuleHandler). That is orthogonal
        // to the metatype-rendering fix under test here; assert the boundary explicitly so the test
        // documents it rather than silently omitting member access.
        AssertThrows<NotSupportedException>(
            () => { _ = person!.Name; },
            "Member access on a Swift-backed composition existential is not supported");
        TestLogger.Info("Closure-param composition existential return resolved its (any A & B).self wrapper symbol");
    }

    #endregion

    #region Owned class-bound proxy GC-finalizer release (SBW_SwiftUnknownObjectRelease trampoline)

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // Create-and-abandon owned (`_ownsContainer == true`) class-bound proxies in a
    // non-inlined helper so the last proxy is NOT kept GC-rooted on the calling test
    // method's frame (Mono's conservative stack scan would otherwise pin it). NO
    // Dispose — the whole point is to drive ReleaseAdoptedSwiftContainer through the
    // GC finalizer (~Proxy) thread, where a direct swift_unknownObjectRelease P/Invoke
    // crashes Mono with the !ji->async assertion after CallConvSwift JIT contamination.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonOwnedSwiftProxies(RetainingCollidableVendor vendor, int n)
    {
        for (int i = 0; i < n; i++)
            _ = vendor.BorrowCollidableIf(true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonOwnedObjCProxies(ObjCBoundVendor vendor, int n)
    {
        for (int i = 0; i < n; i++)
            _ = vendor.BorrowCollidableIf(true);
    }

    public void TestSwiftClassBoundProxyFinalizerReleasesWithoutCrash()
    {
        // The vendor holds the SOLE strong ref and hands the SAME instance back at +1 each
        // call, so every abandoned proxy owns a distinct adopted +1 that its finalizer must
        // balance. Finalizing hundreds of them exercises the class-bound release on the GC
        // finalizer thread en masse — the exact path that SIGABRTs under a direct
        // swift_unknownObjectRelease P/Invoke. Surviving the drain proves the
        // SBW_SwiftUnknownObjectRelease trampoline route is finalizer-safe; reading the
        // vendor's retained instance afterward proves no over-release corrupted the shared +1.
        var vendor = new RetainingCollidableVendor("finalized-swift");
        DrainFinalizers();

        AbandonOwnedSwiftProxies(vendor, 500);
        DrainFinalizers();

        AssertEqual("finalized-swift", vendor.GetRetainedLabel(),
            "vendor instance survived 500 abandoned-proxy finalizations (no finalizer crash, no over-release)");
        TestLogger.Info("500 owned Swift class-bound proxies finalized via trampoline without crash");
    }

    public void TestObjCClassBoundProxyFinalizerReleasesWithoutCrash()
    {
        // Same surviving-owner finalizer probe, but the conformer is NSObject-derived so the
        // adopted +1 is an Objective-C retain. The finalizer release MUST reach objc_release
        // via swift_unknownObjectRelease's isa dispatch — a native swift_release would mis-handle
        // the ObjC refcount — AND it must do so through the finalizer-safe @_cdecl trampoline
        // rather than a direct P/Invoke. This is the highest-risk finalizer path in the subsystem.
        var vendor = new ObjCBoundVendor();
        DrainFinalizers();

        AbandonOwnedObjCProxies(vendor, 500);
        DrainFinalizers();

        AssertEqual("objc-shared", vendor.GetRetainedLabel(),
            "ObjC vendor instance survived 500 abandoned-proxy finalizations (objc_release balanced, no finalizer crash)");
        TestLogger.Info("500 owned ObjC class-bound proxies finalized via trampoline without crash");
    }

    #endregion
}
