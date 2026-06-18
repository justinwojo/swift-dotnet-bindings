// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Finding 11 — the borrow leak. A Copy-semantics runtime wrapper (<c>SwiftResult</c>/<c>SwiftArray</c>)
/// passed BY VALUE into a C# callback is read through the borrowed callback-arg marshal
/// (<c>MarshalCallbackArg&lt;…&gt;</c>). The wrapper's from-handle ctor runs <c>NativeMemory.Alloc</c> +
/// <c>InitializeWithCopy</c>, owning the native buffer plus a +1 on the embedded payload. The old
/// borrowed path blanket-suppressed the wrapper's SafeHandle finalizer, foreclosing the VWT Destroy and
/// leaking that copy per invocation; the declared <c>PayloadConstructionSemantics.Copy</c> contract now
/// keeps the finalizer so the buffer + embedded ref are released.
///
/// This is the opposite direction from <see cref="WireCarrierLeakProbeTests"/> (which probes the
/// Copy-wrapper *return* path). Here Swift invokes the callback <c>count</c> times, each time marshalling
/// a fresh borrowed wrapper into C#; the lambda reads-and-discards it WITHOUT disposing, so cleanup is
/// the finalizer's job alone. Each payload embeds a <see cref="LifetimeTracker"/>-counted
/// <c>TrackedRef</c>, so a suppressed Destroy is a non-zero live count after a GC drain — a deterministic
/// leak (the pinned ref is never released regardless of GC timing), not a flaky "does not crash".
///
/// The single Swift call runs in a <c>[MethodImpl(NoInlining)]</c> helper so no stale stack slot keeps
/// the last borrowed wrapper rooted past the drain.
/// </summary>
public class BorrowedCallbackArgLeakProbeTests : TestBase
{
    public BorrowedCallbackArgLeakProbeTests(TestResults results) : base(results) { }

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
    /// <c>Result&lt;TrackedRef, TrackedRefError&gt;</c> (the SwiftResult Copy wrapper) passed by value into
    /// the callback: the borrowed wrapper's finalizer must run the VWT Destroy and release the embedded
    /// ref. The old suppress-on-borrow path pinned one ref per invocation.
    /// </summary>
    public void TestBorrowedResultCallbackArgReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        InvokeBorrowedResults(1000);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("borrowed SwiftResult Copy-wrapper callback arg must not leak the InitializeWithCopy buffer + embedded ref");
        TestLogger.Info("borrowed SwiftResult callback arg: 1000 invocations released their embedded ref");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeBorrowedResults(int count)
    {
        // Read-and-discard the borrowed Copy-wrapper without disposing — cleanup is the wrapper
        // finalizer's responsibility (the declared Copy semantics keep it; the old borrowed path
        // suppressed it and leaked the copy).
        TestLibFunctions.InvokeWithBorrowedTrackedResult(count, r => { _ = r; });
    }

    /// <summary>
    /// <c>[TrackedRef]</c> (the SwiftArray Copy wrapper) passed by value into the callback: the borrowed
    /// wrapper's finalizer must release its +1 on the CoW storage that backs the element. The old
    /// suppress-on-borrow path pinned the element per invocation.
    /// </summary>
    public void TestBorrowedArrayCallbackArgReleasesElement()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        InvokeBorrowedArrays(1000);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("borrowed SwiftArray Copy-wrapper callback arg must not leak the InitializeWithCopy CoW-storage retain");
        TestLogger.Info("borrowed SwiftArray callback arg: 1000 invocations released their element ref");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeBorrowedArrays(int count)
    {
        TestLibFunctions.InvokeWithBorrowedTrackedArray(count, arr => { _ = arr; });
    }
}
