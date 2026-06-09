// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for justinwojo/swift-dotnet-bindings#40 (Kidoz interstitial crash).
///
/// <para>
/// When Swift calls back into a C# protocol implementation with a method whose parameter
/// is a Swift <b>class</b> instance, the generated proxy receiver used to marshal it with a
/// naive <c>Unsafe.Read&lt;T&gt;</c> — reinterpreting the Swift heap pointer as a managed
/// reference, which SIGSEGVs the first time the reference is used (method dispatch, GC write
/// barrier, or a property read). Strings dodged this via a special case; concrete Swift
/// classes had no branch and fell through the broken <c>_ =&gt; null</c> fallback.
/// </para>
///
/// <para>
/// These tests implement the generated receiver interface, let a Swift driver call back into
/// it, and <b>read a property off the received instance</b> — the exact operation that crashed
/// in the field. The <c>@objc … : NSObject</c> variant is the literal Kidoz <c>KidozError</c>
/// shape and is the one that exercises the ObjC-aware retain (<c>swift_unknownObjectRetain</c>)
/// half of the fix; native-only <c>swift_retain</c> is a no-op / over-release on an NSObject
/// subclass. Leak assertions verify the ARC fix, not merely the absence of a crash.
/// </para>
/// </summary>
public class ClassParamCallbackTests : TestBase
{
    public ClassParamCallbackTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// Drain for <c>@objc:NSObject</c> managed peers that are released by FINALIZATION rather
    /// than an explicit <c>Dispose</c>. Microsoft.iOS defers the native peer release to the
    /// main-thread finalization queue, which only drains on a runloop iteration — so a plain
    /// GC drain runs the C# finalizer but the native <c>dealloc</c> (and the test lib's
    /// <c>recordTrackedDeallocation</c>) never fires. Pumping the runloop after each GC lets
    /// those deferred deallocs complete. Pure-Swift wrappers use a <c>SwiftSafeHandle</c> whose
    /// finalizer releases inline, so they need only the plain <see cref="DrainFinalizers"/>.
    /// </summary>
    private static void DrainObjCFinalizers()
    {
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.05));
        }
    }

    // ---- Pure-Swift class parameter (ClassProjection) ----

    /// <summary>
    /// The core repro: a pure-Swift class flows Swift → C# through the reverse-callback
    /// receiver. Reading <c>.Code</c> / <c>.Label</c> off the received instance is the
    /// operation that dereferenced garbage pre-fix.
    /// </summary>
    public void TestPureSwiftClassParamReceived()
    {
        var impl = new ClassParamReceiverImpl();
        var driver = new ClassParamDriver();

        driver.Drive(impl, code: 42, label: "hello");

        AssertTrue(impl.DidReceiveCalled, "didReceive(_:) fired into the C# impl");
        AssertEqual(42, impl.LastCode, "read .Code off the received pure-Swift class instance");
        AssertEqual("hello", impl.LastLabel, "read .Label off the received pure-Swift class instance");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional&lt;class&gt; receiver branch with a non-nil payload.</summary>
    public void TestPureSwiftOptionalClassParamReceived_NonNil()
    {
        var impl = new ClassParamReceiverImpl();
        var driver = new ClassParamDriver();

        driver.DriveOptional(impl, code: 7, label: "opt");

        AssertTrue(impl.DidReceiveOptionalCalled, "didReceiveOptional(_:) fired");
        AssertTrue(impl.LastOptionalWasPresent, "optional payload delivered non-nil");
        AssertEqual(7, impl.LastCode, "read .Code off the received Optional<class> instance");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional&lt;class&gt; receiver branch with a nil payload (must not crash).</summary>
    public void TestPureSwiftOptionalClassParamReceived_Nil()
    {
        var impl = new ClassParamReceiverImpl();
        var driver = new ClassParamDriver();

        driver.DriveOptionalNil(impl);

        AssertTrue(impl.DidReceiveOptionalCalled, "didReceiveOptional(_:) fired");
        AssertFalse(impl.LastOptionalWasPresent, "optional payload delivered nil");
        GC.KeepAlive(impl);
    }

    // ---- @objc : NSObject class parameter (ObjCRootedClassProjection — the Kidoz shape) ----

    /// <summary>
    /// The literal Kidoz repro: an <c>@objc … : NSObject</c> class flows through the reverse
    /// callback. This is the variant that requires <c>swift_unknownObjectRetain</c> — a native
    /// <c>swift_retain</c> is wrong on an NSObject subclass.
    /// </summary>
    public void TestObjCClassParamReceived()
    {
        var impl = new ObjCClassParamReceiverImpl();
        var driver = new ObjCClassParamDriver();

        driver.Drive(impl, code: 99, label: "objc");

        AssertTrue(impl.DidReceiveCalled, "didReceiveObjC(_:) fired into the C# impl");
        AssertEqual(99, impl.LastCode, "read .Code off the received @objc:NSObject instance (Kidoz repro)");
        AssertEqual("objc", impl.LastLabel, "read .Label off the received @objc:NSObject instance");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional&lt;@objc class&gt; receiver branch with a non-nil payload.</summary>
    public void TestObjCOptionalClassParamReceived_NonNil()
    {
        var impl = new ObjCClassParamReceiverImpl();
        var driver = new ObjCClassParamDriver();

        driver.DriveOptional(impl, code: 11, label: "objc-opt");

        AssertTrue(impl.DidReceiveOptionalCalled, "didReceiveObjCOptional(_:) fired");
        AssertTrue(impl.LastOptionalWasPresent, "optional @objc payload delivered non-nil");
        AssertEqual(11, impl.LastCode, "read .Code off the received Optional<@objc class> instance");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional&lt;@objc class&gt; receiver branch with a nil payload (must not crash).</summary>
    public void TestObjCOptionalClassParamReceived_Nil()
    {
        var impl = new ObjCClassParamReceiverImpl();
        var driver = new ObjCClassParamDriver();

        driver.DriveOptionalNil(impl);

        AssertTrue(impl.DidReceiveOptionalCalled, "didReceiveObjCOptional(_:) fired");
        AssertFalse(impl.LastOptionalWasPresent, "optional @objc payload delivered nil");
        GC.KeepAlive(impl);
    }

    // ---- ARC balance on the reverse-callback receiver path ----

    /// <summary>
    /// The copy-out for a received class param must take exactly one independent retain that
    /// the C# wrapper releases on finalization. Driving many callbacks whose payloads are not
    /// retained by the impl must leave zero live objects once the wrappers drain.
    /// </summary>
    public void TestPureSwiftClassParamReceiverNoLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DrivePureSwiftCallbacks(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("pure-Swift class reverse-callback must not leak the copied-out payload");
        TestLogger.Info("pure-Swift class reverse-callback: 200 payloads copied out and released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DrivePureSwiftCallbacks(int n)
    {
        var impl = new ClassParamReceiverImpl();
        var driver = new ClassParamDriver();
        for (int i = 0; i < n; i++)
            driver.Drive(impl, code: i, label: "x");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// ARC balance for the <c>@objc:NSObject</c> receiver path — verifies the
    /// <c>UnknownObjectRetain</c> fix balances on an NSObject subclass (native
    /// <c>swift_retain</c> would no-op/over-release and skew the live count).
    /// </summary>
    public void TestObjCClassParamReceiverNoLeak()
    {
        DrainObjCFinalizers();
        LifetimeTracker.Reset();

        DriveObjCCallbacks(200);
        // The impl reads scalars but does NOT Dispose the payloads, so each peer is released by
        // finalization — deferred to the main-thread queue for an NSObject peer. Pump the runloop.
        DrainObjCFinalizers();

        LifetimeTracker.AssertNoLeaks("@objc:NSObject class reverse-callback must balance ARC (UnknownObjectRetain)");
        TestLogger.Info("@objc:NSObject class reverse-callback: 200 payloads copied out and released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveObjCCallbacks(int n)
    {
        var impl = new ObjCClassParamReceiverImpl();
        var driver = new ObjCClassParamDriver();
        for (int i = 0; i < n; i++)
            driver.Drive(impl, code: i, label: "x");
        GC.KeepAlive(impl);
    }

    // ---- ARC on the extraction carriers (ExtractCopiedValue / ExtractCopiedElement) ----
    //
    // The fix upgrades two copy-out sites from native swift_retain to the
    // isa-dispatching swift_unknownObjectRetain. Reaching those EXACT sites with an
    // @objc:NSObject payload (so the two retain primitives actually diverge) needs the right
    // carriers — verified against the generated bindings:
    //   • SwiftMarshal.ExtractCopiedValue   ← Result<@objc, Error> read via SwiftResult.Success.
    //   • SwiftMarshal.ExtractCopiedElement ← Optional<(@objc, scalar)> tuple via MarshalTupleFromSwift.
    // Each probe holds the SAME instance in a Swift global, so an over/under-retain shows up as
    // the global's live count diverging from 1 — fully synchronous (explicit Dispose + explicit
    // global clear), so a plain GC drain suffices.

    /// <summary>
    /// <c>Result&lt;@objc class, Error&gt;.Success</c> extraction — the genuine
    /// <c>ExtractCopiedValue</c> probe. The copy-out must take an ObjC-aware
    /// retain; disposing the extracted wrapper + the carrier must leave the Swift global's
    /// instance live (live == 1). A native <c>swift_retain</c> on the NSObject subclass fails to
    /// register the +1, so the extracted wrapper's Dispose over-releases the global's instance.
    /// </summary>
    public void TestObjCResultSuccessExtractionBalancesArc()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var result = TestLibFunctions.StashSharedObjCRefAndReturnResult(99, "r"))
        {
            AssertTrue(result.IsSuccess, "Result<@objc,Error> surfaced .success");
            var extracted = result.Success;
            AssertEqual(99, extracted.Code, "read .Code off @objc extracted from Result.Success (ExtractCopiedValue)");
            extracted.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "ExtractCopiedValue must swift_unknownObjectRetain the @objc copy; the Swift global still owns the shared ref");

        TestLibFunctions.ClearSharedObjCExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared @objc ref");

        TestLogger.Info("Result<@objc,Error>.Success: extracted-copy + carrier Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>Optional&lt;(@objc class, scalar)&gt;</c> extraction — the genuine
    /// <c>ExtractCopiedElement</c> probe. Wrapping the tuple in
    /// <c>Optional</c> routes it through the runtime <c>MarshalTupleFromSwift</c> (unlike a bare
    /// tuple, which the emitter unrolls), so the class element is copied out of a borrowed tuple
    /// slot. Disposing the extracted element must leave the Swift global's instance live.
    /// </summary>
    public void TestObjCOptionalTupleExtractionBalancesArc()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var extracted = TestLibFunctions.StashSharedObjCRefAndReturnOptionalTuple(77);
        AssertTrue(extracted.HasValue, "Optional<(@objc, scalar)> surfaced a value");
        AssertEqual(77, extracted!.Value.Item1.Code, "read .Item1.Code off @objc extracted from Optional<tuple> (ExtractCopiedElement)");
        extracted.Value.Item1.Dispose();
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "ExtractCopiedElement must swift_unknownObjectRetain the @objc element; the Swift global still owns the shared ref");

        TestLibFunctions.ClearSharedObjCExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared @objc ref");

        TestLogger.Info("Optional<(@objc, scalar)>: extracted-element Dispose left the global-owned ref intact");
    }

    // ---- Adjacent return paths (NOT extraction sites — see ClassParamCallback.swift) ----

    /// <summary>
    /// <c>Optional&lt;@objc class&gt;</c> return marshalled inline as
    /// <c>result == IntPtr.Zero ? null : GetINativeObject&lt;T&gt;(result, true)</c>. Functional
    /// coverage: the value reads back correctly and nil surfaces as null. This is NOT an
    /// <c>ExtractCopiedValue</c> probe — see <see cref="TestObjCResultSuccessExtractionBalancesArc"/>
    /// for that. The ARC balance of THIS path is pinned by
    /// <see cref="TestOptionalObjCPayloadReturnNoLeak_KnownFixA"/>.
    /// </summary>
    public void TestOptionalObjCPayloadReturnReads()
    {
        using var p = TestLibFunctions.MakeOptionalObjCPayload(99, "c");
        AssertNotNull(p, "non-nil Optional<@objc class> return surfaces a wrapper");
        AssertEqual(99, p!.Code, "read .Code off @objc payload from adopting Optional return");

        AssertNull(TestLibFunctions.MakeOptionalObjCPayloadNil(), "nil Optional<@objc class> return surfaces as null");
    }

    /// <summary>
    /// Regression guard for "Fix A": the <c>Optional&lt;@objc class&gt;</c> return path must ADOPT the
    /// Swift wrapper's <c>passRetained</c> +1, not add a second retain. Before the fix the path emitted
    /// bare <c>GetNSObject&lt;T&gt;(ptr)</c> (owns:false), which adds its own +1 (DangerousRetain) on top
    /// of the wrapper's unbalanced +1 — so even an explicit <c>Dispose</c> left one net retain per call
    /// and the payloads leaked. <c>OptionalProjection</c> now emits
    /// <c>GetINativeObject&lt;T&gt;(ptr, true)</c> (owns:true), adopting that single +1 so Dispose/finalize
    /// releases exactly once. This is a SEPARATE return-direction leak from the issue #40 receiver crash;
    /// allocating N payloads and disposing each must leave zero live.
    /// </summary>
    public void TestOptionalObjCPayloadReturnNoLeak_KnownFixA()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int lastCode = AllocOptionalObjCPayloads(100);
        AssertEqual(99, lastCode, "read .Code off @objc payload from Optional return");

        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks("Optional<@objc class> adopting return must balance ARC (Fix A regression guard)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocOptionalObjCPayloads(int n)
    {
        int lastCode = -1;
        for (int i = 0; i < n; i++)
        {
            var p = TestLibFunctions.MakeOptionalObjCPayload(i, "c");
            if (p != null)
            {
                lastCode = p.Code;
                p.Dispose();
            }
        }
        return lastCode;
    }

    /// <summary>
    /// Non-optional <c>(@objc class, scalar)</c> tuple return. The emitter UNROLLS this per element
    /// (direct <c>MarshalFromSwift</c> at each element offset) — it does NOT route through
    /// <c>MarshalTupleFromSwift</c>, so it does NOT reach <c>ExtractCopiedElement</c>
    /// (see <see cref="TestObjCOptionalTupleExtractionBalancesArc"/> for that). Kept as independent
    /// coverage of the unrolled tuple path: reading <c>.Item1.Code</c> must return live data and
    /// disposing the element must drain the wrappers to zero.
    /// </summary>
    public void TestObjCPayloadCodeTupleReturnReadsAndReleases()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int lastCode = AllocObjCPayloadCodeTuples(100);
        AssertEqual(99, lastCode, "read .Item1.Code off @objc payload from unrolled tuple return");

        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks("(@objc class, scalar) unrolled tuple return must balance ARC");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocObjCPayloadCodeTuples(int n)
    {
        int lastCode = -1;
        for (int i = 0; i < n; i++)
        {
            var tuple = TestLibFunctions.MakeObjCPayloadCodeTuple(i, "c");
            lastCode = tuple.Item1.Code;
            tuple.Item1.Dispose();
        }
        return lastCode;
    }

    /// <summary>
    /// Array&lt;@objc class&gt; return: covers the <c>SwiftArray.Get</c> element path (whose
    /// subscript getter returns an already-owned +1 element, so it adopts via
    /// <c>MarshalFromSwift</c> with no <c>Arc.Retain</c>) — <b>not</b> <c>ExtractCopiedElement</c>.
    /// Kept as independent coverage of the (already-correct) array path: reading each element's
    /// <c>.Code</c> must return live data and disposing the elements + the array carrier must
    /// drain to zero.
    /// </summary>
    public void TestObjCPayloadArrayReturnReadsAndReleases()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int seen = AllocAndReadObjCPayloadArrays(50, 5);
        AssertEqual(50 * 5, seen, "read .Code off every @objc array element (SwiftArray.Get path)");

        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks("[@objc class] array extraction must balance ARC (SwiftArray.Get +1-owned subscript getter)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocAndReadObjCPayloadArrays(int iterations, int elementsPerCall)
    {
        int seen = 0;
        for (int i = 0; i < iterations; i++)
        {
            var list = TestLibFunctions.MakeObjCPayloadArray(elementsPerCall);
            try
            {
                foreach (var element in list)
                {
                    _ = element.Code;
                    seen++;
                    element.Dispose();
                }
            }
            finally
            {
                (list as IDisposable)?.Dispose();
            }
        }
        return seen;
    }
}

/// <summary>
/// C# implementation of the generated <c>IClassParamReceiver</c>. Records the values read off
/// the received instance so the test can prove the reverse-callback delivered a usable object,
/// not a garbage reference. Deliberately does NOT retain the payload object itself — only its
/// scalar fields — so the leak tests can observe the wrapper draining.
/// </summary>
internal sealed class ClassParamReceiverImpl : IClassParamReceiver
{
    public bool DidReceiveCalled { get; private set; }
    public bool DidReceiveOptionalCalled { get; private set; }
    public bool LastOptionalWasPresent { get; private set; }
    public int LastCode { get; private set; }
    public string LastLabel { get; private set; } = "";

    public void DidReceive(ClassParamPayload payload)
    {
        DidReceiveCalled = true;
        LastCode = payload.Code;
        LastLabel = payload.Label.ToString();
    }

    public void DidReceiveOptional(ClassParamPayload? payload)
    {
        DidReceiveOptionalCalled = true;
        LastOptionalWasPresent = payload != null;
        if (payload != null)
        {
            LastCode = payload.Code;
            LastLabel = payload.Label.ToString();
        }
    }
}

/// <summary>
/// C# implementation of the generated <c>IObjCClassParamReceiver</c> (the Kidoz
/// <c>@objc:NSObject</c> shape).
/// </summary>
internal sealed class ObjCClassParamReceiverImpl : IObjCClassParamReceiver
{
    public bool DidReceiveCalled { get; private set; }
    public bool DidReceiveOptionalCalled { get; private set; }
    public bool LastOptionalWasPresent { get; private set; }
    public int LastCode { get; private set; }
    public string LastLabel { get; private set; } = "";

    public void DidReceiveObjC(ObjCClassParamPayload payload)
    {
        DidReceiveCalled = true;
        LastCode = payload.Code;
        LastLabel = payload.Label.ToString();
    }

    public void DidReceiveObjCOptional(ObjCClassParamPayload? payload)
    {
        DidReceiveOptionalCalled = true;
        LastOptionalWasPresent = payload != null;
        if (payload != null)
        {
            LastCode = payload.Code;
            LastLabel = payload.Label.ToString();
        }
    }
}
