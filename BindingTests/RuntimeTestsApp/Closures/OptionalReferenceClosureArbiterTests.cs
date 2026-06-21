// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// End-to-end gate for the closure-argument ownership convergence: every closure-argument site routes
/// through the single shared narrow predicate <c>ClosureHandler.IsOptionalReferenceArg</c>
/// (<c>Optional&lt;T&gt;</c> with a true-reference inner). The lowering in
/// <c>ClosureEmitter.GetInvokeArgExpression</c> runs for BOTH the @_cdecl and the CallConvSwift
/// closure-callback shapes.
///
/// <para>
/// For an <c>Optional&lt;class&gt;</c> inner the convergence routes the +1 marshal consistently:
/// pure-Swift class via <c>MarshalBorrowedClassFromSwift</c> (<c>EmitSwift</c>), ObjC-rooted
/// (<c>@objc … : NSObject</c>) class via <c>MarshalCallbackArg</c> (its <c>Kind == Class</c> upgrade,
/// <c>EmitObjC</c>) — the same isa-aware +1 the non-optional reference arm and MethodClosureBridge use.
/// Before the convergence the ObjC-rooted arm used <c>FormatObjCBridgeCall</c> (<c>GetNSObject</c>);
/// both round-trip and balance ARC for such a dual-natured NSObject peer, so this is a consistency
/// convergence — and the first regression coverage of either ObjC-rooted or pure-Swift
/// <c>Optional&lt;class&gt;</c> closure arguments.
/// </para>
///
/// <para>
/// OUT OF SCOPE — <c>Optional&lt;ObjC-bridgeable VALUE type&gt;</c> closure arguments (<c>URL?</c>,
/// <c>URLRequest?</c>). A closure slot carries such an inner by its Swift VALUE representation, not as
/// an object pointer (no Swift-side <c>as AnyObject</c> bridge on the reverse-closure path), so reading
/// it via <c>GetNSObject&lt;NSUrl&gt;</c> would SIGABRT (<c>_objc_fatal</c>). The closure-argument
/// predicate is therefore the narrow <c>IsOptionalReferenceArg</c>, distinct from the WIDER
/// producer-position oracle <c>WrapperValidation.IsOptionalWithReferenceInner</c>; the unit-layer delta
/// is pinned by <c>OptionalReferenceClassifierTests</c>. Value-type closure arguments need a separate
/// Swift bridging thunk (pre-existing, never-worked; tracked in roadmap.md) and are deliberately not
/// fixtured here.
/// </para>
///
/// <para>
/// The leak tests pin the +0/+1 ownership pairing on the class arms — not merely the absence of a crash.
/// </para>
/// </summary>
public class OptionalReferenceClosureArbiterTests : TestBase
{
    public OptionalReferenceClosureArbiterTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // ---- @objc : NSObject optional closure argument (the arbiter) ----

    /// <summary>
    /// Non-nil <c>Optional&lt;@objc class&gt;</c> closure argument round-trips: the closure must
    /// receive a usable wrapper and read live <c>.Code</c> / <c>.Label</c> off it — the operation
    /// that would dereference a mis-marshalled handle pre-fix.
    /// </summary>
    public void TestEmitObjC_NonNil_RoundTrips()
    {
        var lab = new OptionalReferenceClosureArbiter();
        ClosureOptionalObjCPayload? captured = null;
        global::System.Action<ClosureOptionalObjCPayload?> completion = p => captured = p;

        lab.EmitObjC(true, completion);

        AssertNotNull(captured, "non-nil Optional<@objc class> closure arg delivers a wrapper");
        AssertEqual(7, captured!.Code, "read .Code off the @objc payload received in the closure");
        AssertEqual("rooted", captured!.Label.ToString(), "read .Label off the @objc payload received in the closure");
        GC.KeepAlive(lab);
    }

    /// <summary>Nil <c>Optional&lt;@objc class&gt;</c> closure argument surfaces as null (must not crash).</summary>
    public void TestEmitObjC_Nil_SurfacesNull()
    {
        var lab = new OptionalReferenceClosureArbiter();
        bool fired = false;
        ClosureOptionalObjCPayload? captured = null;
        global::System.Action<ClosureOptionalObjCPayload?> completion = p =>
        {
            fired = true;
            captured = p;
        };

        lab.EmitObjC(false, completion);

        AssertTrue(fired, "completion fired for the nil @objc closure arg");
        AssertNull(captured, "nil Optional<@objc class> closure arg surfaces as null");
        GC.KeepAlive(lab);
    }

    // ---- Pure-Swift optional closure argument (control) ----

    /// <summary>
    /// Control: a pure-Swift (non-rooted) <c>Optional&lt;class&gt;</c> closure argument — the arm
    /// that already routed to <c>MarshalBorrowedClassFromSwift</c> — must keep round-tripping.
    /// </summary>
    public void TestEmitSwift_NonNil_RoundTrips()
    {
        var lab = new OptionalReferenceClosureArbiter();
        ClosureOptionalSwiftPayload? captured = null;
        global::System.Action<ClosureOptionalSwiftPayload?> completion = p => captured = p;

        lab.EmitSwift(true, completion);

        AssertNotNull(captured, "non-nil Optional<pure-Swift class> closure arg delivers a wrapper");
        AssertEqual(11, captured!.Code, "read .Code off the pure-Swift payload received in the closure");
        GC.KeepAlive(lab);
    }

    /// <summary>Control: nil pure-Swift <c>Optional&lt;class&gt;</c> closure arg surfaces as null.</summary>
    public void TestEmitSwift_Nil_SurfacesNull()
    {
        var lab = new OptionalReferenceClosureArbiter();
        bool fired = false;
        ClosureOptionalSwiftPayload? captured = null;
        global::System.Action<ClosureOptionalSwiftPayload?> completion = p =>
        {
            fired = true;
            captured = p;
        };

        lab.EmitSwift(false, completion);

        AssertTrue(fired, "completion fired for the nil pure-Swift closure arg");
        AssertNull(captured, "nil Optional<pure-Swift class> closure arg surfaces as null");
        GC.KeepAlive(lab);
    }

    // ---- ARC balance on the optional closure-arg paths ----

    /// <summary>
    /// The matched-pair ownership invariant, observed through explicit <c>Dispose</c>. The Swift
    /// wrapper passes the inner <c>passUnretained</c> (+0 borrow) and the C# marshal
    /// (<c>MarshalCallbackArg</c> → <c>MarshalBorrowedClassFromSwift</c>) takes its own
    /// isa-aware +1, building an <b>owning</b> wrapper. Disposing that wrapper inside the closure
    /// body must release exactly that one +1 — freeing the payload (its Swift <c>deinit</c> runs)
    /// with no double-release of the borrowed +0. An over-release would crash; a missing release leaks.
    /// <para>
    /// Dispose is required because an <c>@objc … : NSObject</c> wrapper is a Microsoft.iOS peer whose
    /// owned native reference roots the managed peer — GC alone cannot reclaim it, so this asserts the
    /// <em>deterministic</em> Dispose path rather than finalizer timing.
    /// </para>
    /// </summary>
    public void TestEmitObjC_DisposeInClosure_FreesExactlyOnce()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveObjCClosuresDisposing(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Optional<@objc class> closure arg must balance ARC (passUnretained/+0 ↔ MarshalCallbackArg/+1) on explicit Dispose");
        TestLogger.Info("Optional<@objc class> closure arg: 200 payloads delivered, disposed, and freed exactly once");
    }

    /// <summary>
    /// Control: a pure-Swift (non-rooted) <c>Optional&lt;class&gt;</c> closure argument routes to
    /// <c>MarshalBorrowedClassFromSwift</c> and builds an owning wrapper backed by a
    /// <c>SwiftSafeHandle</c>, so its +1 is balanced on <b>finalization</b> — no explicit Dispose
    /// required. Driving many callbacks whose payloads escape neither the closure body nor a handle
    /// must drain to zero live objects after finalizers run.
    /// </summary>
    public void TestEmitSwift_NonNil_NoLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveSwiftClosures(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Optional<pure-Swift class> closure arg must balance ARC via finalization");
        TestLogger.Info("Optional<pure-Swift class> closure arg: 200 payloads delivered and finalized");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveObjCClosuresDisposing(int n)
    {
        var lab = new OptionalReferenceClosureArbiter();
        int sum = 0;
        global::System.Action<ClosureOptionalObjCPayload?> completion = p =>
        {
            if (p != null)
            {
                sum += p.Code;
                p.Dispose();
            }
        };
        for (int i = 0; i < n; i++)
            lab.EmitObjC(true, completion);
        TestLogger.Info($"drove {n} @objc closure args, disposing each (code sum {sum})");
        GC.KeepAlive(lab);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveSwiftClosures(int n)
    {
        var lab = new OptionalReferenceClosureArbiter();
        int sum = 0;
        global::System.Action<ClosureOptionalSwiftPayload?> completion = p =>
        {
            if (p != null)
                sum += p.Code;
        };
        for (int i = 0; i < n; i++)
            lab.EmitSwift(true, completion);
        TestLogger.Info($"drove {n} pure-Swift closure args (code sum {sum})");
        GC.KeepAlive(lab);
    }
}
