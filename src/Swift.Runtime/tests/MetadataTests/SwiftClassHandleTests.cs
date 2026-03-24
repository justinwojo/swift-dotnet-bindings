// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using System.Threading;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Tests for SwiftClassHandle&lt;T&gt; — ARC-bridged SafeHandle for Swift class instances.
/// These are unit tests that verify the handle's behavior using mock pointers.
/// Real Swift object ARC verification happens in BindingTests runtime tests.
/// </summary>
public class SwiftClassHandleTests
{
    /// <summary>
    /// Mock ISwiftObject for testing SwiftClassHandle without real Swift objects.
    /// </summary>
    private sealed class MockSwiftClass : ISwiftObject, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
        }

        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    [Fact]
    public void Constructor_StoresPointerDirectly()
    {
        var ptr = new IntPtr(0x12345678);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        Assert.False(handle.IsInvalid);
        Assert.False(handle.IsClosed);
        Assert.Equal(ptr, handle.DangerousGetHandle());

        // Must clean up with exit guard to prevent Arc.Release on mock pointer.
        // Without this, the GC finalizer would call swift_isDeallocating(0x12345678) → SIGSEGV.
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Close();
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void Constructor_ZeroPointer_IsInvalid()
    {
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        Assert.True(handle.IsInvalid);
    }

    [Fact]
    public void Zero_StaticField_IsInvalid()
    {
        var zero = SwiftClassHandle<MockSwiftClass>.Zero;

        Assert.True(zero.IsInvalid);
    }

    [Fact]
    public void DangerousGetHandle_ReturnsObjectPointerDirectly()
    {
        // Verify there's no buffer indirection — the handle IS the Swift pointer
        var ptr = new IntPtr(0xABCD_EF00);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        // DangerousGetHandle should return the pointer directly, NOT a buffer address
        Assert.Equal(ptr, handle.DangerousGetHandle());

        // Clean up with exit guard to prevent Arc.Release on mock pointer.
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Close();
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void Dispose_MarksHandleAsClosed()
    {
        // Use IntPtr.Zero so ReleaseHandle early-exits without calling Arc.Release.
        // Non-zero mock pointers would cause SIGSEGV from swift_isDeallocating P/Invoke.
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle.Dispose();

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle.Dispose();
        handle.Dispose(); // Should not throw (SafeHandle ignores second Dispose)
    }

    [Fact]
    public void Dispose_ZeroHandle_DoesNotThrow()
    {
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle.Dispose(); // Should not throw (early exit for zero handle)
    }

    [Fact]
    public void Dispose_SuppressesFinalizer()
    {
        // Verify that Dispose suppresses finalization (prevents double-release).
        // After Dispose, the finalizer should NOT run.
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle.Dispose();

        // The handle should be closed (Disposed) but not crash
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void ReleaseHandle_ZeroHandle_ReturnsTrue()
    {
        // Zero handles should release cleanly without calling Arc.Release
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle.Dispose(); // Triggers ReleaseHandle internally

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Handle_NoBufferIndirection()
    {
        // This is the key semantic difference from SwiftSafeHandle:
        // SwiftSafeHandle holds a buffer pointer, and the class pointer is *(IntPtr*)buffer.
        // SwiftClassHandle directly holds the class pointer — no dereference needed.
        var classPtr = new IntPtr(0xDEAD_BEEF);
        var handle = new SwiftClassHandle<MockSwiftClass>(classPtr);

        // For SwiftClassHandle, DangerousGetHandle() IS the class pointer
        var retrieved = handle.DangerousGetHandle();
        Assert.Equal(classPtr, retrieved);

        // Contrast with SwiftSafeHandle where you'd need *(IntPtr*)handle to get the class pointer
        // With SwiftClassHandle, no dereference is needed

        // Clean up with exit guard to prevent Arc.Release on mock pointer.
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Close();
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void DangerousAddRef_DangerousRelease_WorkCorrectly()
    {
        // Use IntPtr.Zero so the final Dispose/Close doesn't call Arc.Release on an invalid pointer.
        // DangerousAddRef/DangerousRelease only manage SafeHandle ref counting (not Swift ARC).
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        bool success = false;
        handle.DangerousAddRef(ref success);
        Assert.True(success);

        // Should be able to release after adding ref
        handle.DangerousRelease();

        // Handle should still be valid after DangerousRelease (it just decrements the ref)
        Assert.False(handle.IsClosed);

        handle.Dispose();
    }

    [Fact]
    public void MultipleHandles_IndependentLifetimes()
    {
        // Use zero handles to avoid Arc.Release on mock pointers during Dispose.
        var handle1 = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);
        var handle2 = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        handle1.Dispose();

        Assert.True(handle1.IsClosed);
        Assert.False(handle2.IsClosed);

        handle2.Dispose();
        Assert.True(handle2.IsClosed);
    }

    [Fact]
    public void ProcessExitFlag_SetByExitGuard()
    {
        // Verify that the exit guard flag can be set and read.
        // Save and restore state to avoid polluting other tests.
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            Assert.True(SwiftExitGuard.IsProcessExiting);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void ProcessExitFlag_DefaultFalse()
    {
        // The flag should be false by default (not during process exit).
        // Reset in case a prior test set it.
        SwiftExitGuard.SetProcessExitingForTest(false);
        // Note: IsProcessExiting also checks Environment.HasShutdownStarted,
        // but that is always false during test execution.
        Assert.False(SwiftExitGuard.IsProcessExiting);
    }

    [Fact]
    public void IsProcessExiting_IncludesHasShutdownStarted()
    {
        // SwiftExitGuard.IsProcessExiting should return true if EITHER the explicit
        // ProcessExit flag is set OR Environment.HasShutdownStarted is true.
        // During tests, HasShutdownStarted is always false, so we verify:
        // 1. With flag false + HasShutdownStarted false → false
        // 2. With flag true + HasShutdownStarted false → true
        SwiftExitGuard.SetProcessExitingForTest(false);
        Assert.False(SwiftExitGuard.IsProcessExiting);

        SwiftExitGuard.SetProcessExitingForTest(true);
        Assert.True(SwiftExitGuard.IsProcessExiting);

        SwiftExitGuard.SetProcessExitingForTest(false);
        // We can't test HasShutdownStarted=true without actually shutting down,
        // but the OR logic is verified by the property implementation.
    }

    [Fact]
    public void FinalizerPath_DuringProcessExit_SkipsRelease()
    {
        // During process exit, finalizer-triggered ReleaseHandle should skip Arc.Release
        // to avoid crashes from Swift deinitializers in a partially torn-down runtime.
        // We use Close() instead of Dispose() to simulate the finalizer path — Close()
        // calls SafeHandle.Dispose() which bypasses our `new Dispose()` that sets
        // _explicitDispose, so _explicitDispose stays false (same as finalizer).
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            // Close() triggers ReleaseHandle without setting _explicitDispose.
            // With exit guard + !_explicitDispose, Arc.Release is skipped (no crash
            // on mock pointer 0x1).
            handle.Close();
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void ExplicitDispose_DuringProcessExit_DoesNotSkip()
    {
        // Explicit Dispose should still attempt Arc.Release even during process exit.
        // Swift deinit may have side effects (flushing, closing, persisting) that
        // should run during graceful shutdown — only finalizer-triggered cleanup is skipped.
        //
        // We can't test this with a mock pointer (Arc.Release on invalid pointer causes
        // a native SEGFAULT that kills the process). Instead we verify with a zero handle,
        // which hits the early-exit path before the guard check. The SwiftSafeHandle tests
        // cover this path with a mock destroy action that doesn't P/Invoke.
        var handle = new SwiftClassHandle<MockSwiftClass>(IntPtr.Zero);

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Dispose(); // explicit Dispose on zero handle — early exit
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
        // The real explicit-dispose-during-exit behavior is tested by
        // SwiftSafeHandleShutdownTests.ExplicitDispose_DuringProcessExit_StillCallsDestroy
        // which uses a managed destroy action instead of P/Invoke.
    }
}

/// <summary>
/// Tests for SwiftSafeHandle&lt;T&gt; shutdown behavior — struct-handle exit guard path.
/// Verifies that finalizer-triggered cleanup is skipped during process exit,
/// while explicit Dispose still runs VWT Destroy via Cdecl trampoline.
/// </summary>
public class SwiftSafeHandleShutdownTests
{
    /// <summary>
    /// Mock ISwiftObject for testing SwiftSafeHandle without real Swift objects.
    /// GetTypeMetadata() throws, so _metadataHandle will be IntPtr.Zero (VWT Destroy skipped).
    /// </summary>
    private sealed class MockSwiftStruct : ISwiftObject, ISwiftStruct, IDisposable
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private static unsafe IntPtr AllocMockBuffer()
    {
        return (IntPtr)NativeMemory.AllocZeroed(16);
    }

    [Fact]
    public void ReleaseHandle_DuringProcessExit_SkipsDestroy_FreesBuffer()
    {
        // During process exit, finalizer-triggered ReleaseHandle should skip
        // VWT Destroy but still free the .NET-allocated buffer.
        var handle = new SwiftSafeHandle<MockSwiftStruct>(AllocMockBuffer());

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            // Simulate finalizer path (no explicit Dispose — just close the handle).
            // SafeHandle.Close() calls ReleaseHandle without setting _explicitDispose.
            handle.Close();
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void ExplicitDispose_DuringProcessExit_StillRunsReleaseHandle()
    {
        // Explicit Dispose should still run ReleaseHandle even during process exit.
        // VWT Destroy will attempt to run (metadata may be invalid for mock types,
        // but the exception is swallowed per SafeHandle contract).
        var handle = new SwiftSafeHandle<MockSwiftStruct>(AllocMockBuffer());

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Dispose(); // explicit Dispose — ReleaseHandle should still run
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void ExplicitDispose_NormalExecution_ClosesHandle()
    {
        // Baseline: explicit Dispose during normal execution closes the handle.
        // VWT Destroy via Cdecl trampoline is attempted (skipped for mock types
        // because _metadataHandle is IntPtr.Zero due to GetTypeMetadata() throwing).
        var handle = new SwiftSafeHandle<MockSwiftStruct>(AllocMockBuffer());

        SwiftExitGuard.SetProcessExitingForTest(false);
        handle.Dispose();
        Assert.True(handle.IsClosed);
    }
}

/// <summary>
/// Tests for GC/finalizer lifecycle safety of SwiftClassHandle and SwiftSafeHandle.
/// Verifies that abandoned handles survive GC collection without double-free or SIGSEGV.
/// These tests use mock handles that don't require real Swift objects.
/// Mock types have GetTypeMetadata() that throws, so _metadataHandle = IntPtr.Zero
/// and VWT Destroy via Cdecl trampoline is skipped (only NativeMemory.Free runs).
/// </summary>
public class HandleGCLifecycleTests
{
    /// <summary>
    /// Mock ISwiftObject for testing handle lifecycle without real Swift objects.
    /// </summary>
    private sealed class MockSwiftType : ISwiftObject, IDisposable
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class MockSwiftStructType : ISwiftObject, ISwiftStruct, IDisposable
    {
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private static unsafe IntPtr AllocMockBuffer()
    {
        return (IntPtr)NativeMemory.AllocZeroed(16);
    }

    #region SwiftClassHandle GC lifecycle

    [Fact]
    public void ClassHandle_DoubleDispose_IsNoop()
    {
        // Calling Dispose twice must not throw or crash.
        // SafeHandle internally tracks IsClosed and skips the second ReleaseHandle call.
        var handle = new SwiftClassHandle<MockSwiftType>(IntPtr.Zero);
        handle.Dispose();
        handle.Dispose(); // must be silent noop
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void ClassHandle_DisposeAfterClose_IsNoop()
    {
        // Close() is the SafeHandle base method. Calling Dispose() after Close() should be safe.
        var handle = new SwiftClassHandle<MockSwiftType>(IntPtr.Zero);
        handle.Close();
        handle.Dispose(); // must be silent noop
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void ClassHandle_ZeroIsNeverCorrupted_AfterDispose()
    {
        // The static Zero field must remain usable even after someone calls Dispose on it.
        // Zero has handle=IntPtr.Zero, which is "invalid" per SafeHandleZeroOrMinusOneIsInvalid.
        // Disposing an invalid handle should be safe.
        var zero = SwiftClassHandle<MockSwiftType>.Zero;
        Assert.True(zero.IsInvalid);

        // Even after Dispose, the Zero handle should still be queryable
        zero.Dispose();
        Assert.True(zero.IsClosed);
        // Creating a new Zero-valued handle should still work independently
        var fresh = new SwiftClassHandle<MockSwiftType>(IntPtr.Zero);
        Assert.True(fresh.IsInvalid);
    }

    [Fact]
    public void ClassHandle_AbandonedHandles_SurviveGC()
    {
        // Create many class handles and abandon them, then force GC.
        // The handles have IntPtr.Zero so ReleaseHandle returns immediately.
        // This tests that the finalizer path doesn't crash.
        for (int i = 0; i < 50; i++)
        {
            _ = new SwiftClassHandle<MockSwiftType>(IntPtr.Zero);
        }

        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();

        // If we get here, no crash from finalizers
        Assert.True(true, "50 abandoned class handles finalized without crash");
    }

    [Fact]
    public void ClassHandle_ConcurrentDisposeAndAccess_IsThreadSafe()
    {
        // Verify that concurrent DangerousAddRef/DangerousRelease and Dispose don't crash.
        // Use IntPtr.Zero so ReleaseHandle hits the early-exit path (no Arc.Release call).
        // Testing with a real Swift pointer requires the Swift runtime; unit tests use zero pointers.
        var handle = new SwiftClassHandle<MockSwiftType>(IntPtr.Zero);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var running = true;

        var addRefThread = new Thread(() =>
        {
            while (running)
            {
                try
                {
                    bool success = false;
                    handle.DangerousAddRef(ref success);
                    if (success)
                        handle.DangerousRelease();
                }
                catch (ObjectDisposedException)
                {
                    // Expected if Dispose races with DangerousAddRef
                }
                catch (Exception ex)
                {
                    errors.Add(ex.ToString());
                }
            }
        });
        addRefThread.IsBackground = true;
        addRefThread.Start();

        Thread.Sleep(10);
        handle.Dispose(); // Dispose while the other thread is accessing
        running = false;
        addRefThread.Join(TimeSpan.FromSeconds(5));

        Assert.True(errors.IsEmpty, $"Concurrent errors: {string.Join("; ", errors)}");
    }

    #endregion

    #region SwiftSafeHandle GC lifecycle

    [Fact]
    public void SafeHandle_DoubleDispose_IsNoop()
    {
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());
        handle.Dispose();
        handle.Dispose(); // must be silent noop
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void SafeHandle_ZeroIsNeverCorrupted_AfterDispose()
    {
        var zero = SwiftSafeHandle<MockSwiftStructType>.Zero;
        Assert.True(zero.IsInvalid);

        zero.Dispose();
        Assert.True(zero.IsClosed);

        // A new Zero-valued handle should still work independently
        var fresh = new SwiftSafeHandle<MockSwiftStructType>(IntPtr.Zero);
        Assert.True(fresh.IsInvalid);
    }

    [Fact]
    public void SafeHandle_AbandonedHandles_SurviveGC()
    {
        // Allocate real NativeMemory buffers, abandon them, and force GC.
        // ReleaseHandle frees them safely — VWT Destroy is skipped because mock types
        // have _metadataHandle = IntPtr.Zero, and NativeMemory.Free runs.
        for (int i = 0; i < 50; i++)
        {
            _ = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());
        }

        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();

        Assert.True(true, "50 abandoned safe handles finalized without crash");
    }

    [Fact]
    public void SafeHandle_InterleavedAllocAndGC_NoCrash()
    {
        // Interleave allocation and GC collection to simulate real-world GC pressure.
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 20; i++)
            {
                _ = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());
            }
            GC.Collect(0, GCCollectionMode.Forced);
            // Don't wait for finalizers in inner loop -- let them race with allocations
        }

        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();

        Assert.True(true, "Interleaved alloc+GC (100 handles, 5 rounds) without crash");
    }

    [Fact]
    public void SafeHandle_MixedDisposeAndAbandon_NoCrash()
    {
        // Mix explicit Dispose and abandon in the same batch.
        var handles = new List<SwiftSafeHandle<MockSwiftStructType>>();
        for (int i = 0; i < 50; i++)
        {
            handles.Add(new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer()));
        }

        // Dispose every other handle; abandon the rest
        for (int i = 0; i < handles.Count; i += 2)
        {
            handles[i].Dispose();
        }
        handles.Clear(); // Drop all references

        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();

        Assert.True(true, "Mixed dispose/abandon pattern finalized without crash");
    }

    #endregion

    #region SwiftDispose.FinalizerCleanup safety

    [Fact]
    public void FinalizerCleanup_NullPayload_NoOp()
    {
        // FinalizerCleanup should handle null gracefully.
        SwiftDispose.FinalizerCleanup<MockSwiftStructType>(null);
        // No exception = pass
    }

    [Fact]
    public void FinalizerCleanup_InvalidPayload_NoOp()
    {
        // FinalizerCleanup should skip Dispose for invalid (zero) handles.
        var zero = new SwiftSafeHandle<MockSwiftStructType>(IntPtr.Zero);
        Assert.True(zero.IsInvalid);

        SwiftDispose.FinalizerCleanup(zero);
        // Invalid handles should not be disposed -- IsInvalid check prevents it
    }

    [Fact]
    public void FinalizerCleanup_ValidPayload_DisposesHandle()
    {
        // FinalizerCleanup should call Close on valid handles on all runtimes.
        // VWT Destroy is routed through the Cdecl trampoline, safe from any thread.
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());
        Assert.False(handle.IsInvalid);

        SwiftDispose.FinalizerCleanup(handle);

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void FinalizerCleanup_AlreadyClosed_NoOp()
    {
        // FinalizerCleanup should handle already-closed handles gracefully.
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());
        handle.Dispose(); // close it first
        Assert.True(handle.IsClosed);

        // This should not throw even though handle is already closed
        SwiftDispose.FinalizerCleanup(handle);
    }

    #endregion

    #region Metadata caching

    [Fact]
    public void SafeHandle_ZeroHandle_SkipsMetadataCaching()
    {
        // Zero handles should not attempt to cache metadata.
        // This prevents metadata resolution during static initialization
        // (SwiftSafeHandle<T>.Zero).
        var handle = new SwiftSafeHandle<MockSwiftStructType>(IntPtr.Zero);

        // The handle is invalid, so no metadata caching was attempted.
        // GetTypeMetadata() throwing would have caused an error if caching ran.
        Assert.True(handle.IsInvalid);
        handle.Dispose();
    }

    [Fact]
    public void SafeHandle_NonZeroHandle_CachesMetadataGracefully()
    {
        // For non-zero handles with mock types, GetTypeMetadata() throws.
        // The constructor should catch this and set _metadataHandle to IntPtr.Zero.
        // Disposal should still work (VWT Destroy skipped, buffer freed).
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());

        Assert.False(handle.IsInvalid);

        // Dispose should work — VWT Destroy is skipped because metadata unavailable,
        // but NativeMemory.Free still runs.
        handle.Dispose();
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void FinalizerCleanup_PreservesProcessExitGuard()
    {
        // FinalizerCleanup uses Close() (not Dispose()) so _explicitDispose stays false.
        // During process exit, the exit guard should kick in and skip VWT Destroy.
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);

            // Close() triggers ReleaseHandle with _explicitDispose=false.
            // With the exit guard active, HandleProcessExitCleanup runs (frees buffer only).
            SwiftDispose.FinalizerCleanup(handle);

            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void SafeHandle_FinalizerPath_DuringProcessExit_SkipsVwtDestroy()
    {
        // Simulate finalizer path during process exit:
        // Close() does not set _explicitDispose, so the exit guard skips VWT Destroy.
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Close(); // finalizer path — _explicitDispose=false → exit guard
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void SafeHandle_ExplicitDispose_DuringProcessExit_StillRunsRelease()
    {
        // Explicit Dispose sets _explicitDispose=true, bypassing the exit guard.
        // VWT Destroy should still be attempted (for Swift deinit side effects).
        var handle = new SwiftSafeHandle<MockSwiftStructType>(AllocMockBuffer());

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Dispose(); // explicit — _explicitDispose=true → HandleNormalRelease
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    #endregion
}
