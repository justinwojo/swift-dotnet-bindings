// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end coverage for the ProtocolExtensionClosureBridge (PExtCB) emitter:
/// protocol extension methods taking an @escaping closure must close the
/// alloc-before-P/Invoke leak window and route the GCHandle through the
/// `_SBClosureCtx` owner-token so the deinit upcall frees the handle.
/// </summary>
public class ProtocolExtensionClosureTests : TestBase
{
    public ProtocolExtensionClosureTests(TestResults results) : base(results) { }

    public void TestEscapingVoidInvokedExactlyOnce()
    {
        using var seed = new PExtClosureSeed(seed: 0);
        var fired = 0;
        seed.RunEscapingVoid(() => fired++);
        AssertEqual(1, fired, "Extension escaping closure invoked exactly once");
    }

    public void TestEscapingVoidRepeatedCallsDoNotLeak()
    {
        // Re-invoking the bridge many times relies on `_SBClosureCtx` deinit
        // freeing each per-call GCHandle; if the throw-window fix or box wiring
        // regressed we'd accumulate dead handles and trip GC pressure.
        using var seed = new PExtClosureSeed(seed: 0);
        var fired = 0;
        for (var i = 0; i < 64; i++)
        {
            seed.RunEscapingVoid(() => fired++);
        }
        AssertEqual(64, fired, "Each repeated invocation runs the closure exactly once");
    }

    /// <summary>
    /// Owner-token contract: capture a tracked object inside the C# delegate,
    /// drop the local reference, force GC. The captured object must deallocate
    /// because the per-call GCHandle is freed by the `_SBClosureCtx` deinit
    /// upcall after the wrapper returns.
    /// </summary>
    [SkipOnSimulator("Requires the SwiftBindingsRuntime native framework; the simulator build sets IncludeSwiftBindingsRuntimeNative=false (InstallNameTool workaround), so the destroy hook degrades to the documented leak fallback. Validated on device (NativeAOT).")]
    public void TestEscapingVoidReleasesCapturedTrackedObject()
    {
        LifetimeTracker.Reset();

        using var seed = new PExtClosureSeed(seed: 0);

        // Capture in a separate frame that is popped before collecting: nulling a local is not
        // enough under a conservative stack scan, which reads the raw slot rather than the
        // variable. This is the same shape AutoWrappedDelegateTests uses for its weak-slot probe.
        CaptureTrackedObjectInEscapingClosure(seed);

        // Collect from a worker thread whose stack never held the object. NativeAOT scans
        // precisely, so plain GC.Collect() sufficed there; the device Mono full-AOT lane scans
        // the stack conservatively and a stale pointer left in this thread's frame poses as a
        // root, reporting a leak the bindings did not cause.
        ForceGCThorough();

        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        TestLogger.Info($"PExtCB closure capture lifetime: alloc={alloc} dealloc={dealloc} live={live}");
        AssertEqual(0, live, "Captured TrackedObject deallocated after PExtCB call");
    }

    /// <summary>
    /// The protocol-extension bridge is the one closure bridge whose Swift adapter copies every
    /// non-primitive callback argument into a scratch buffer and passes that buffer's ADDRESS, where
    /// the other bridges pass the instance pointer. Reading the address as the instance wraps the
    /// scratch buffer — which Swift deallocates when the callback returns — so the arriving object
    /// has to be asserted on a real field value, not merely on being non-null.
    /// </summary>
    public void TestClosureArgClassIsReadThroughTheSlot()
    {
        using var seed = new PExtClosureSeed(seed: 4242);

        SwiftBindingsTestLib.PExtClosureItem? captured = null;
        seed.InspectItem(item => captured = item);

        AssertTrue(captured is not null, "Protocol-extension closure delivered the class argument");
        AssertEqual(4242, captured!.Value, "PExtClosureItem.Value read through the borrowed slot");
    }

    /// <summary>
    /// The same slot convention for a BOUND GENERIC whose base is a Swift class. Every reference predicate
    /// declines a type spec carrying generic arguments, so the flavour is taken from the declaration the
    /// spec names and the emitted read is that declaration's class reader — which has to carry the slot
    /// convention with it, or it retains and wraps the scratch buffer instead of the boxed instance.
    /// Reading the box's payload through to the inner class's own field is what makes a buffer-as-instance
    /// read observable rather than merely non-null.
    /// </summary>
    public void TestClosureArgBoundGenericIsReadThroughTheSlot()
    {
        using var seed = new PExtClosureSeed(seed: 1234);

        SwiftBindingsTestLib.PExtClosureBox<SwiftBindingsTestLib.PExtClosureItem>? captured = null;
        seed.InspectBoxedItem(box => captured = box);

        AssertTrue(captured is not null, "Protocol-extension closure delivered the bound-generic argument");
        using var boxed = captured!.Value;
        AssertEqual(1234, boxed.Value, "PExtClosureBox payload read through the borrowed slot");
    }

    /// <summary>
    /// The bound generic closed over an argument with no Swift metadata — the shape that breaks a reader
    /// which infers class-ness from metadata, because a generic class's accessor needs metadata for every
    /// argument and an ObjC peer has none. With the lookup failing, the payload arm adopts the scratch
    /// buffer address as the instance handle, so comparing the wrapper's handle against the address Swift
    /// recorded for the very object it passed separates a real instance from the buffer that carried it.
    /// </summary>
    public void TestClosureArgBoundGenericOverMetadatalessArgIsReadThroughTheSlot()
    {
        using var seed = new PExtClosureSeed(seed: 0);

        SwiftBindingsTestLib.PExtClosureBox<Foundation.NSUrlResponse>? captured = null;
        seed.InspectBoxedResponse(box => captured = box);

        AssertTrue(captured is not null, "Protocol-extension closure delivered the metadata-less bound generic");
        using var boxed = captured!;
        AssertEqual(PExtClosureProbe.LastBoxedResponseAddress,
            (ulong)boxed.Payload.DangerousGetHandle().ToInt64(),
            "Delivered box wraps the Swift instance, not the scratch buffer that carried it");
    }

    /// <summary>
    /// The same slot convention for a reference the Swift runtime cannot see at all: an Objective-C
    /// peer with no Swift type-metadata record. It has to bridge onto the ObjC peer registry off the
    /// pointer the slot HOLDS; bridging off the slot address itself registers the scratch buffer.
    /// Asserting the concrete subclass plus URL and status code means a wrong dereference cannot pass
    /// by producing some live object.
    /// </summary>
    public void TestClosureArgObjCPeerIsReadThroughTheSlot()
    {
        using var seed = new PExtClosureSeed(seed: 0);

        Foundation.NSUrlResponse? captured = null;
        seed.InspectResponse(response => captured = response);

        AssertTrue(captured is not null, "Protocol-extension closure delivered the ObjC peer argument");
        AssertTrue(captured is Foundation.NSHttpUrlResponse,
            $"Expected the delivered peer to be an NSHttpUrlResponse, got {captured!.GetType().Name}");

        var http = (Foundation.NSHttpUrlResponse)captured!;
        AssertEqual(PExtClosureProbe.ResponseStatus, (int)http.StatusCode, "StatusCode read through the slot");
        AssertEqual<string?>(PExtClosureProbe.ResponseUrl, http.Url?.AbsoluteString, "Url.AbsoluteString read through the slot");
    }

    /// <summary>
    /// Creates the tracked object, captures it in the escaping delegate, runs the bridge, and
    /// returns — so on return no live frame references the object. The closure delegate's
    /// GCHandle is then the only remaining root path to it, and only for as long as the
    /// <c>_SBClosureCtx</c> deinit upcall has not yet freed that handle.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CaptureTrackedObjectInEscapingClosure(PExtClosureSeed seed)
    {
        var captured = TestLibFunctions.CreateTrackedObject(7777);
        var (_, _, liveBefore) = LifetimeTracker.GetStats();
        AssertTrue(liveBefore >= 1, "TrackedObject created (live >= 1)");

        seed.RunEscapingVoid(() => { _ = captured.IsAlive(); });
    }
}
