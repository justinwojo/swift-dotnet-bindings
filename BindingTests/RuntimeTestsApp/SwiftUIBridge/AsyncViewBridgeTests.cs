// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for async chain SwiftUI bridge Views.
/// Validates async inference, callback-based creation, and lifecycle management.
/// </summary>
public class BridgeAsyncViewTests : TestBase
{
    public BridgeAsyncViewTests(TestResults results) : base(results) { }

    [Skip("Async finalizer thread SIGSEGV in SwiftClassHandle.ReleaseHandle during Arc.Release — crashes both runtimes")]
    public async Task TestAsyncServiceView()
    {
        var handle = await WithTimeout(CreateAsyncServiceView("test-key"), DefaultAsyncTimeout);
        AssertTrue(handle != IntPtr.Zero, "AsyncServiceView handle != 0");

        var vcPtr = BridgeNativeMethods.AsyncServiceView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "AsyncServiceView GetVC != 0");

        BridgeNativeMethods.AsyncServiceView_Free(handle);

        // Verify dispose behavior
        var vcAfterFree = BridgeNativeMethods.AsyncServiceView_GetViewController(handle);
        // After free, handle is removed from liveHandles — GetVC returns nil
        AssertTrue(vcAfterFree == IntPtr.Zero, "AsyncServiceView GetVC == 0 after free");

        TestLogger.Info("AsyncServiceView: create/validate/free cycle passed");
    }

    [Skip("Async finalizer thread SIGSEGV in SwiftClassHandle.ReleaseHandle during Arc.Release — crashes both runtimes")]
    public async Task TestDeepChainView()
    {
        var handle = await WithTimeout(CreateDeepChainView("test-key", 42), DefaultAsyncTimeout);
        AssertTrue(handle != IntPtr.Zero, "DeepChainView handle != 0");

        var vcPtr = BridgeNativeMethods.DeepChainView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "DeepChainView GetVC != 0");

        BridgeNativeMethods.DeepChainView_Free(handle);
        TestLogger.Info("DeepChainView: create/validate/free cycle passed");
    }

    [Skip("Async finalizer thread SIGSEGV in SwiftClassHandle.ReleaseHandle during Arc.Release — crashes both runtimes")]
    public async Task TestMixedAsyncView()
    {
        var handle = await WithTimeout(CreateMixedAsyncView("test-key", 10, true), DefaultAsyncTimeout);
        AssertTrue(handle != IntPtr.Zero, "MixedAsyncView handle != 0");

        var vcPtr = BridgeNativeMethods.MixedAsyncView_GetViewController(handle);
        AssertTrue(vcPtr != IntPtr.Zero, "MixedAsyncView GetVC != 0");

        BridgeNativeMethods.MixedAsyncView_Free(handle);
        TestLogger.Info("MixedAsyncView: create/validate/free cycle passed");
    }

    #region Async Create Helpers

    private static unsafe Task<IntPtr> CreateAsyncServiceView(string key)
    {
        return AsyncViewHelper.CreateAsyncView((readyPtr, errorPtr, statePtr) =>
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            fixed (byte* keyPtr = keyBytes)
            {
                BridgeNativeMethods.AsyncServiceView_Create(
                    (IntPtr)keyPtr, keyBytes.Length,
                    readyPtr, errorPtr, statePtr);
            }
        });
    }

    private static unsafe Task<IntPtr> CreateDeepChainView(string key, int mode)
    {
        return AsyncViewHelper.CreateAsyncView((readyPtr, errorPtr, statePtr) =>
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            fixed (byte* keyPtr = keyBytes)
            {
                BridgeNativeMethods.DeepChainView_Create(
                    (IntPtr)keyPtr, keyBytes.Length, mode,
                    readyPtr, errorPtr, statePtr);
            }
        });
    }

    private static unsafe Task<IntPtr> CreateMixedAsyncView(string key, int count, bool enabled)
    {
        return AsyncViewHelper.CreateAsyncView((readyPtr, errorPtr, statePtr) =>
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            fixed (byte* keyPtr = keyBytes)
            {
                BridgeNativeMethods.MixedAsyncView_Create(
                    (IntPtr)keyPtr, keyBytes.Length, count, enabled ? 1 : 0,
                    readyPtr, errorPtr, statePtr);
            }
        });
    }

    #endregion
}

#endif
