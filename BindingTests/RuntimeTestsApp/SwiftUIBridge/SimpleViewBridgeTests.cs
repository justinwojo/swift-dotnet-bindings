// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for simple (non-async) SwiftUI bridge Views.
/// Validates BoundEnum, BoundType, TypedClosure, optional variants, and mixed params.
/// </summary>
public class BridgeSimpleViewTests : TestBase
{
    public BridgeSimpleViewTests(TestResults results) : base(results) { }

    public void TestEnumParamView()
    {
        var handle = BridgeNativeMethods.EnumParamView_Create(1); // AlertStyle.warning
        AssertTrue(handle != IntPtr.Zero, "EnumParamView handle != 0");

        var style = BridgeTestHelpers.EnumParamView_GetStyle(handle);
        AssertEqual(1, style, "EnumParamView style round-trip");

        var vcPtr = BridgeNativeMethods.EnumParamView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "EnumParamView GetVC != 0");

        BridgeNativeMethods.EnumParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("EnumParamView: create/read/free cycle passed");
    }

    public void TestClassParamView()
    {
        var modelPtr = BridgeTestHelpers.CreateSimpleModel(99);
        AssertTrue(modelPtr != IntPtr.Zero, "CreateSimpleModel != 0");

        var handle = BridgeNativeMethods.ClassParamView_Create(modelPtr);
        AssertTrue(handle != IntPtr.Zero, "ClassParamView handle != 0");

        var value = BridgeTestHelpers.ClassParamView_GetModelValue(handle);
        AssertEqual(99, value, "ClassParamView model value round-trip");

        BridgeNativeMethods.ClassParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        TestLogger.Info("ClassParamView: create/read/free cycle passed");
    }

    public unsafe void TestTypedClosureView()
    {
        TypedClosureState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, int> callbackPtr = &TypedClosureState.OnValueCallback;
        var handle = BridgeNativeMethods.TypedClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "TypedClosureView handle != 0");

        var result = BridgeTestHelpers.TypedClosureView_InvokeClosure(handle, 42);
        AssertEqual(1, TypedClosureState.CallCount, "TypedClosureView callback fired");
        AssertEqual(42, TypedClosureState.LastArgValue, "TypedClosureView arg round-trip");
        AssertEqual(1, result, "TypedClosureView: 42 -> true -> 1");

        BridgeNativeMethods.TypedClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("TypedClosureView: create/invoke/free cycle passed");
    }

    public unsafe void TestMultiArgClosureView()
    {
        MultiArgClosureState.Reset();

        delegate* unmanaged[Cdecl]<int, int, IntPtr, void> callbackPtr = &MultiArgClosureState.OnEventCallback;
        var handle = BridgeNativeMethods.MultiArgClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "MultiArgClosureView handle != 0");

        var result = BridgeTestHelpers.MultiArgClosureView_InvokeClosure(handle, 7, 1);
        AssertEqual(1, MultiArgClosureState.CallCount, "MultiArgClosureView callback fired");
        AssertEqual(7, MultiArgClosureState.LastVal, "MultiArgClosureView val round-trip");
        AssertTrue(MultiArgClosureState.LastFlag, "MultiArgClosureView flag round-trip");
        AssertEqual(1, result, "MultiArgClosureView invoke success");

        BridgeNativeMethods.MultiArgClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("MultiArgClosureView: create/invoke/free cycle passed");
    }

    public unsafe void TestMixedParamView()
    {
        MixedActionState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, void> callbackPtr = &MixedActionState.OnActionCallback;
        var handle = BridgeNativeMethods.MixedParamView_Create(1, (IntPtr)callbackPtr, IntPtr.Zero, 42);
        AssertTrue(handle != IntPtr.Zero, "MixedParamView handle != 0");

        var style = BridgeTestHelpers.MixedParamView_GetStyle(handle);
        AssertEqual(1, style, "MixedParamView style round-trip");

        var count = BridgeTestHelpers.MixedParamView_GetCount(handle);
        AssertEqual(42, count, "MixedParamView count round-trip");

        BridgeTestHelpers.MixedParamView_FireAction(handle);
        // onAction is dispatched async on main queue — pump the run loop to process it
        // (Thread.Sleep blocks the main thread, preventing dispatch processing)
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.5));
        AssertTrue(MixedActionState.CallCount >= 1, "MixedParamView action callback fired");

        BridgeNativeMethods.MixedParamView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("MixedParamView: create/read/callback/free cycle passed");
    }

    public void TestOptionalEnumWithValue()
    {
        var handle = BridgeNativeMethods.OptionalEnumView_Create(1, 2); // hasValue=1, value=2 (error)
        AssertTrue(handle != IntPtr.Zero, "OptionalEnumView handle != 0");

        var hasValue = BridgeTestHelpers.OptionalEnumView_HasValue(handle);
        AssertEqual(1, hasValue, "OptionalEnumView has value");

        var style = BridgeTestHelpers.OptionalEnumView_GetStyle(handle);
        AssertEqual(2, style, "OptionalEnumView style round-trip");

        BridgeNativeMethods.OptionalEnumView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalEnumView (with value): passed");
    }

    public void TestOptionalEnumNil()
    {
        var handle = BridgeNativeMethods.OptionalEnumView_Create(0, 0); // hasValue=0
        AssertTrue(handle != IntPtr.Zero, "OptionalEnumView nil handle != 0");

        var hasValue = BridgeTestHelpers.OptionalEnumView_HasValue(handle);
        AssertEqual(0, hasValue, "OptionalEnumView nil has no value");

        BridgeNativeMethods.OptionalEnumView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalEnumView (nil): passed");
    }

    public void TestOptionalClassWithValue()
    {
        var modelPtr = BridgeTestHelpers.CreateSimpleModel(77);
        var handle = BridgeNativeMethods.OptionalClassView_Create(modelPtr);
        AssertTrue(handle != IntPtr.Zero, "OptionalClassView handle != 0");

        var hasValue = BridgeTestHelpers.OptionalClassView_HasValue(handle);
        AssertEqual(1, hasValue, "OptionalClassView has value");

        var modelValue = BridgeTestHelpers.OptionalClassView_GetModelValue(handle);
        AssertEqual(77, modelValue, "OptionalClassView model value round-trip");

        BridgeNativeMethods.OptionalClassView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        TestLogger.Info("OptionalClassView (with value): passed");
    }

    public void TestOptionalClassNil()
    {
        var handle = BridgeNativeMethods.OptionalClassView_Create(IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "OptionalClassView nil handle != 0");

        var hasValue = BridgeTestHelpers.OptionalClassView_HasValue(handle);
        AssertEqual(0, hasValue, "OptionalClassView nil has no value");

        BridgeNativeMethods.OptionalClassView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalClassView (nil): passed");
    }

    public void TestClassParamLifetime()
    {
        BridgeTestHelpers.ResetSimpleModelDeinitCount();

        var modelPtr = BridgeTestHelpers.CreateSimpleModel(42);
        var sessionHandle = BridgeNativeMethods.ClassParamView_Create(modelPtr);
        AssertTrue(sessionHandle != IntPtr.Zero, "Lifetime: session created");

        // Free original model pointer — session should retain model
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        var afterModelFree = BridgeTestHelpers.GetSimpleModelDeinitCount();
        AssertEqual(0, afterModelFree, "Model alive while session holds it");

        // Free session — model should dealloc
        BridgeNativeMethods.ClassParamView_Free(sessionHandle, IntPtr.Zero, 0, IntPtr.Zero);
        var afterSessionFree = BridgeTestHelpers.GetSimpleModelDeinitCount();
        AssertEqual(1, afterSessionFree, "Model deallocated after session free");

        TestLogger.Info("ClassParamView lifetime: passed");
    }

    /// <summary>
    /// Generated SwiftUI bridge session classes (<c>ClassParamViewSession</c> et al.)
    /// own a native handle obtained via <c>Unmanaged.passRetained</c>. If a consumer
    /// constructs a session and forgets to <c>Dispose</c>, the GC-driven cleanup path
    /// must release the native session and any pinned <c>GCHandle</c>s rather than
    /// leaking them for the process lifetime.
    ///
    /// This test exercises that fallback: it allocates a <c>SimpleModel</c> and a
    /// <c>ClassParamViewSession</c> wrapping it inside a helper, lets the locals fall
    /// out of scope without calling <c>Dispose</c>, then forces a GC loop. The session
    /// finalizer should run <c>Dispose(false)</c> → native <c>Free</c> → Swift session
    /// <c>deinit</c> → drop the retained <c>SimpleModel</c>. Once the C# wrapper's
    /// SafeHandle finalizer also runs, the Swift <c>SimpleModel</c> reaches refcount
    /// zero and increments <c>deinitCount</c>.
    /// </summary>
    public void TestClassParamViewSessionFinalizerReleasesNativeHandle()
    {
        // Drain any SimpleModel instances orphaned by earlier tests before we
        // snapshot the counter. Without this, a straggler from a prior test
        // can finalize after our reset and false-pass the assertion below
        // even if this session's handle leaked.
        PumpFinalizersAndMainRunLoop(rounds: 3);

        BridgeTestHelpers.ResetSimpleModelDeinitCount();
        var baseline = BridgeTestHelpers.GetSimpleModelDeinitCount();
        AssertEqual(0, baseline,
            "deinitCount must be zero after pre-flush + reset — a non-zero baseline " +
            "means a SimpleModel is finalizing between Reset and the Get below, which " +
            "would defeat the per-test correlation.");

        CreateOrphanedSession(); // session+model locals fall out of scope here

        // Two-stage chain: first GC finalizes the session (which calls native Free
        // and drops the +1 retain on SimpleModel); second GC finalizes the C#
        // SimpleModel SafeHandle, which drops the last refcount and lets the Swift
        // SimpleModel reach deinit. Pump the main run loop between GC rounds so
        // the bridge's `_Free` async-dispatched cleanup actually executes — the
        // off-main async dispatch keeps the finalizer thread from deadlocking
        // against a main parked inside GC.WaitForPendingFinalizers, but the
        // resulting block sits queued on main until something spins the loop.
        int deinitCount = 0;
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Foundation.NSRunLoop.Current.RunUntil(Foundation.NSDate.FromTimeIntervalSinceNow(0.1));
            deinitCount = BridgeTestHelpers.GetSimpleModelDeinitCount();
            if (deinitCount >= 1) break;
        }

        AssertTrue(deinitCount >= 1,
            $"SimpleModel.deinitCount must be >= 1 after GC-only session teardown; " +
            $"observed {deinitCount}. A zero count means the session finalizer " +
            $"failed to release the native handle (the regression shape).");

        TestLogger.Info($"ClassParamViewSession finalizer released native handle (deinitCount={deinitCount})");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateOrphanedSession()
    {
        var model = new SimpleModel(value: 1234);
        var session = ClassParamViewSession.Create(model);
        AssertOrphanInvariants(session);
        // Deliberately no Dispose / using — exercises the finalizer path.
    }

    /// <summary>
    /// Pins the architectural fix where Swift owns GCHandle disposal: a session
    /// constructed with onAppear/onDisappear lifecycle callbacks carries pinned
    /// GCHandle pointers inside Swift state. Pre-fix, C# Dispose freed those
    /// handles immediately after invoking the (async-dispatched) native Free,
    /// leaving Swift state holding freed pointers until the queued release
    /// block ran — a UAF window that would crash if any callback fired. Post-fix
    /// the GCHandle disposal is routed through a Swift callback that runs only
    /// after <c>Unmanaged.release</c> completes inside the dispatched block.
    /// This test exercises the orphaned-finalize path with lifecycle handles
    /// attached and asserts no crash plus the same <c>SimpleModel.deinit</c>
    /// invariant the no-callback test enforces.
    /// </summary>
    public void TestClassParamViewSessionWithLifecycleHandlesSurvivesAsyncFreeOrdering()
    {
        PumpFinalizersAndMainRunLoop(rounds: 3);
        BridgeTestHelpers.ResetSimpleModelDeinitCount();
        var baseline = BridgeTestHelpers.GetSimpleModelDeinitCount();
        AssertEqual(0, baseline, "deinitCount must be zero after pre-flush + reset.");

        CreateOrphanedSessionWithLifecycle();

        int deinitCount = 0;
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Foundation.NSRunLoop.Current.RunUntil(Foundation.NSDate.FromTimeIntervalSinceNow(0.1));
            deinitCount = BridgeTestHelpers.GetSimpleModelDeinitCount();
            if (deinitCount >= 1) break;
        }

        AssertTrue(deinitCount >= 1,
            $"SimpleModel.deinitCount must be >= 1 after GC-only teardown of a " +
            $"session with lifecycle handles; observed {deinitCount}. A zero count " +
            $"means the GCHandle-aware Free path leaked the native handle or " +
            $"the Swift-side disposal callback never fired.");

        TestLogger.Info(
            $"ClassParamViewSession with lifecycle handles survived async-Free ordering " +
            $"(deinitCount={deinitCount})");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateOrphanedSessionWithLifecycle()
    {
        var model = new SimpleModel(value: 4321);
        Action onAppear = () => { /* never invoked under GC-only teardown */ };
        Action onDisappear = () => { /* never invoked under GC-only teardown */ };
        var session = ClassParamViewSession.Create(model, onAppear, onDisappear);
        AssertOrphanInvariants(session);
        // Deliberately no Dispose / using — exercises the finalizer + Swift-owned
        // GCHandle disposal path.
    }

    /// <summary>
    /// Direct contract test for the architectural fix. Drives the Swift _Free
    /// wrapper from a BACKGROUND thread (exercises the off-main DispatchQueue.main.async
    /// path) with a sentinel GCHandle in a NativeMemory buffer plus a test-controlled
    /// post-release trampoline. Asserts:
    ///   1. The trampoline fired.
    ///   2. The sentinel GCHandle was still allocated when the trampoline ran
    ///      (the caller did not free it before _Free completed).
    ///   3. <c>GetViewController(sessionHandle)</c> returns <c>IntPtr.Zero</c> from
    ///      inside the trampoline — proving the release block (which removes the
    ///      handle from liveHandles AND calls <c>Unmanaged.release</c>) executed
    ///      BEFORE the trampoline, i.e. ordering is post-release.
    ///   4. The trampoline observed itself running on the main thread (queued
    ///      release block, not the caller's background thread).
    /// </summary>
    public unsafe void TestClassParamViewFreeRunsPostReleaseTrampolineWithLiveHandle()
    {
        OrderedFreeTrampolineState.Reset();

        var sentinel = new object();
        var sentinelHandle = GCHandle.Alloc(sentinel);
        var sentinelPtr = GCHandle.ToIntPtr(sentinelHandle);

        var buffer = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        buffer[0] = sentinelPtr;

        var modelPtr = BridgeTestHelpers.CreateSimpleModel(7777);
        AssertTrue(modelPtr != IntPtr.Zero, "CreateSimpleModel != 0");
        var handle = BridgeNativeMethods.ClassParamView_Create(modelPtr);
        AssertTrue(handle != IntPtr.Zero, "ClassParamView handle != 0");

        // Stash the session handle so the trampoline can probe liveHandles via
        // GetViewController(handle) — returns IntPtr.Zero iff the release block
        // already removed this handle from the live set.
        OrderedFreeTrampolineState.SessionHandle = handle;

        delegate* unmanaged[Cdecl]<IntPtr, int, void> trampolinePtr = &OrderedFreeTrampolineState.OnPostRelease;
        var bufPtr = (IntPtr)buffer;
        var trampolineIntPtr = (IntPtr)trampolinePtr;

        // Call _Free from a background thread to force the off-main DispatchQueue.main.async
        // path. The Swift release block then runs on the main run loop, which we pump below.
        var freeTask = Task.Run(() =>
        {
            BridgeNativeMethods.ClassParamView_Free(handle, bufPtr, 1, trampolineIntPtr);
        });

        for (int i = 0; i < 20; i++)
        {
            Foundation.NSRunLoop.Current.RunUntil(Foundation.NSDate.FromTimeIntervalSinceNow(0.1));
            if (OrderedFreeTrampolineState.Ran) break;
        }
        freeTask.Wait(TimeSpan.FromSeconds(2));

        BridgeTestHelpers.FreeSimpleModel(modelPtr);

        AssertTrue(OrderedFreeTrampolineState.Ran,
            "Post-release trampoline must fire after _Free's release block runs.");
        AssertTrue(OrderedFreeTrampolineState.SentinelWasAllocatedAtTrampoline,
            "Sentinel GCHandle must still be allocated when the post-release " +
            "trampoline runs — proves C# Dispose did NOT free it locally before " +
            "_Free invoked the post-release callback.");
        AssertTrue(OrderedFreeTrampolineState.RanOnMainThread,
            "Trampoline must run on the main thread — proves the off-main " +
            "DispatchQueue.main.async path queued the release block onto main " +
            "(rather than running it inline on the background caller thread).");
        AssertTrue(OrderedFreeTrampolineState.SessionRemovedFromLiveHandlesBeforeTrampoline,
            "GetViewController(sessionHandle) must return IntPtr.Zero from inside " +
            "the trampoline — proves the release block ran (removing handle from " +
            "liveHandles and calling Unmanaged.release) BEFORE the post-release " +
            "trampoline fired. A non-zero observation means trampoline-before-release " +
            "ordering has regressed.");

        TestLogger.Info("ClassParamView _Free post-release trampoline runs post-release on main (architectural ordering pinned)");
    }

    private static unsafe class OrderedFreeTrampolineState
    {
        public static IntPtr SessionHandle;
        public static volatile bool Ran;
        public static volatile bool SentinelWasAllocatedAtTrampoline;
        public static volatile bool RanOnMainThread;
        public static volatile bool SessionRemovedFromLiveHandlesBeforeTrampoline;

        public static void Reset()
        {
            SessionHandle = IntPtr.Zero;
            Ran = false;
            SentinelWasAllocatedAtTrampoline = false;
            RanOnMainThread = false;
            SessionRemovedFromLiveHandlesBeforeTrampoline = false;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void OnPostRelease(IntPtr buffer, int count)
        {
            RanOnMainThread = Foundation.NSThread.IsMain;
            if (SessionHandle != IntPtr.Zero)
            {
                var vc = BridgeNativeMethods.ClassParamView_GetViewController(SessionHandle);
                SessionRemovedFromLiveHandlesBeforeTrampoline = (vc == IntPtr.Zero);
            }
            if (buffer != IntPtr.Zero && count > 0)
            {
                var slots = (IntPtr*)buffer;
                var raw = slots[0];
                if (raw != IntPtr.Zero)
                {
                    var h = GCHandle.FromIntPtr(raw);
                    SentinelWasAllocatedAtTrampoline = h.IsAllocated;
                    if (h.IsAllocated) h.Free();
                }
                NativeMemory.Free((void*)buffer);
            }
            Ran = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertOrphanInvariants(ClassParamViewSession session)
    {
        if (session.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Session handle must be non-zero before finalization");
    }

    private static void PumpFinalizersAndMainRunLoop(int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Foundation.NSRunLoop.Current.RunUntil(Foundation.NSDate.FromTimeIntervalSinceNow(0.05));
        }
    }

    public unsafe void TestStringClosureView()
    {
        StringClosureState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> callbackPtr = &StringClosureState.OnResultCallback;
        var handle = BridgeNativeMethods.StringClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "StringClosureView handle != 0");

        // Invoke the closure via test helper with "hello"
        var testBytes = Encoding.UTF8.GetBytes("hello");
        fixed (byte* testPtr = testBytes)
        {
            var result = BridgeTestHelpers.StringClosureView_InvokeClosure(handle, (IntPtr)testPtr, testBytes.Length);
            AssertEqual(1, result, "StringClosureView invoke success");
        }

        AssertEqual(1, StringClosureState.CallCount, "StringClosureView callback fired");
        AssertEqual("hello", StringClosureState.LastValue!, "StringClosureView string round-trip");

        BridgeNativeMethods.StringClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("StringClosureView: create/invoke/free cycle passed");
    }

    public unsafe void TestClassClosureView()
    {
        ClassClosureState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callbackPtr = &ClassClosureState.OnModelCallback;
        var handle = BridgeNativeMethods.ClassClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "ClassClosureView handle != 0");

        // Create a model and invoke the closure
        var modelPtr = BridgeTestHelpers.CreateSimpleModel(55);
        var result = BridgeTestHelpers.ClassClosureView_InvokeClosure(handle, modelPtr);
        AssertEqual(1, result, "ClassClosureView invoke success");
        AssertEqual(1, ClassClosureState.CallCount, "ClassClosureView callback fired");
        AssertTrue(ClassClosureState.LastModelPtr != IntPtr.Zero, "ClassClosureView received model pointer");

        BridgeNativeMethods.ClassClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        BridgeTestHelpers.FreeSimpleModel(modelPtr);
        TestLogger.Info("ClassClosureView: create/invoke/free cycle passed");
    }

    public unsafe void TestOptionalStringWithValue()
    {
        var titleBytes = Encoding.UTF8.GetBytes("test title");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.OptionalStringView_Create((IntPtr)titlePtr, titleBytes.Length);
            AssertTrue(handle != IntPtr.Zero, "OptionalStringView handle != 0");

            var hasValue = BridgeTestHelpers.OptionalStringView_HasValue(handle);
            AssertEqual(1, hasValue, "OptionalStringView has value");

            var titleLen = BridgeTestHelpers.OptionalStringView_GetTitleLength(handle);
            AssertEqual(10, titleLen, "OptionalStringView title length");

            BridgeNativeMethods.OptionalStringView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("OptionalStringView (with value): passed");
    }

    public void TestOptionalStringNil()
    {
        var handle = BridgeNativeMethods.OptionalStringView_Create(IntPtr.Zero, 0);
        AssertTrue(handle != IntPtr.Zero, "OptionalStringView nil handle != 0");

        var hasValue = BridgeTestHelpers.OptionalStringView_HasValue(handle);
        AssertEqual(0, hasValue, "OptionalStringView nil has no value");

        BridgeNativeMethods.OptionalStringView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalStringView (nil): passed");
    }

    public void TestOptionalStringEmpty()
    {
        // Pass non-null pointer with length 0 → empty string (not nil)
        var emptyBytes = new byte[1]; // dummy byte to get non-null pointer
        unsafe
        {
            fixed (byte* emptyPtr = emptyBytes)
            {
                var handle = BridgeNativeMethods.OptionalStringView_Create((IntPtr)emptyPtr, 0);
                AssertTrue(handle != IntPtr.Zero, "OptionalStringView empty handle != 0");

                var hasValue = BridgeTestHelpers.OptionalStringView_HasValue(handle);
                AssertEqual(1, hasValue, "OptionalStringView empty has value");

                var titleLen = BridgeTestHelpers.OptionalStringView_GetTitleLength(handle);
                AssertEqual(0, titleLen, "OptionalStringView empty title length");

                BridgeNativeMethods.OptionalStringView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
            }
        }
        TestLogger.Info("OptionalStringView (empty): passed");
    }

    public unsafe void TestOptionalClosureWithCallback()
    {
        OptionalClosureState.Reset();

        delegate* unmanaged[Cdecl]<int, IntPtr, void> callbackPtr = &OptionalClosureState.OnCallback;
        var handle = BridgeNativeMethods.OptionalClosureView_Create((IntPtr)callbackPtr, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "OptionalClosureView handle != 0");

        var result = BridgeTestHelpers.OptionalClosureView_InvokeClosure(handle, 99);
        AssertEqual(1, result, "OptionalClosureView callback exists");
        AssertEqual(1, OptionalClosureState.CallCount, "OptionalClosureView callback fired");
        AssertEqual(99, OptionalClosureState.LastValue, "OptionalClosureView arg round-trip");

        BridgeNativeMethods.OptionalClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalClosureView (with callback): passed");
    }

    public void TestOptionalClosureNil()
    {
        // When callback function pointer is nil, the bridge still creates a non-nil wrapper closure
        // (optional chaining makes cb_callback?(...) a no-op). The View's callback is non-nil
        // but calling it does nothing. This is by design — closures are already nullable in the ABI.
        OptionalClosureState.Reset();

        var handle = BridgeNativeMethods.OptionalClosureView_Create(IntPtr.Zero, IntPtr.Zero);
        AssertTrue(handle != IntPtr.Zero, "OptionalClosureView nil handle != 0");

        // The closure exists but invoking it is a no-op (cb_callback is nil)
        var result = BridgeTestHelpers.OptionalClosureView_InvokeClosure(handle, 42);
        AssertEqual(1, result, "OptionalClosureView wrapper closure exists");
        // The inner C callback was never called (it was nil)
        AssertEqual(0, OptionalClosureState.CallCount, "OptionalClosureView nil cb not invoked");

        BridgeNativeMethods.OptionalClosureView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        TestLogger.Info("OptionalClosureView (nil): passed");
    }

    public unsafe void TestMixedStringView()
    {
        StringClosureState.Reset();

        delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> callbackPtr = &StringClosureState.OnResultCallback;
        var titleBytes = Encoding.UTF8.GetBytes("mixed title");
        fixed (byte* titlePtr = titleBytes)
        {
            var handle = BridgeNativeMethods.MixedStringView_Create(
                (IntPtr)titlePtr, titleBytes.Length,
                (IntPtr)callbackPtr, IntPtr.Zero);
            AssertTrue(handle != IntPtr.Zero, "MixedStringView handle != 0");

            var titleLen = BridgeTestHelpers.MixedStringView_GetTitleLength(handle);
            AssertEqual(11, titleLen, "MixedStringView title length");

            // Invoke closure
            var resultBytes = Encoding.UTF8.GetBytes("result");
            fixed (byte* resultPtr = resultBytes)
            {
                var result = BridgeTestHelpers.MixedStringView_InvokeClosure(handle, (IntPtr)resultPtr, resultBytes.Length);
                AssertEqual(1, result, "MixedStringView invoke success");
            }

            AssertEqual(1, StringClosureState.CallCount, "MixedStringView callback fired");
            AssertEqual("result", StringClosureState.LastValue!, "MixedStringView result round-trip");

            BridgeNativeMethods.MixedStringView_Free(handle, IntPtr.Zero, 0, IntPtr.Zero);
        }
        TestLogger.Info("MixedStringView: create/read/invoke/free cycle passed");
    }
}

#endif
