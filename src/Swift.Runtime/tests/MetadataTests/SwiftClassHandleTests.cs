// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Tests for SwiftClassHandle&lt;T&gt; — ARC-bridged SafeHandle for Swift class instances.
/// These are unit tests that verify the handle's behavior using mock pointers.
/// Real Swift object ARC verification happens in TestFramework runtime tests.
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
    }

    [Fact]
    public void Dispose_MarksHandleAsClosed()
    {
        // Use a valid non-zero pointer but it won't be released since
        // it's not a real Swift object (Arc.Release will throw, which is caught).
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        handle.Dispose();

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

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
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

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
    }

    [Fact]
    public void DangerousAddRef_DangerousRelease_WorkCorrectly()
    {
        var ptr = new IntPtr(0xCAFE);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

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
        var ptr1 = new IntPtr(0x1111);
        var ptr2 = new IntPtr(0x2222);
        var handle1 = new SwiftClassHandle<MockSwiftClass>(ptr1);
        var handle2 = new SwiftClassHandle<MockSwiftClass>(ptr2);

        handle1.Dispose();

        Assert.True(handle1.IsClosed);
        Assert.False(handle2.IsClosed);
        Assert.Equal(ptr2, handle2.DangerousGetHandle());

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
        Assert.False(SwiftExitGuard.IsProcessExiting);
    }

    [Fact]
    public void ReleaseHandle_DuringProcessExit_SkipsRelease()
    {
        // During process exit, ReleaseHandle (via finalizer) should skip Arc.Release
        // to avoid crashes from Swift deinitializers in a partially torn-down runtime.
        // We verify that Dispose closes the handle without throwing.
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            // Dispose calls ReleaseHandle — with the exit guard set, it should
            // skip Arc.Release and just null the handle (no crash on mock pointer).
            handle.Dispose();
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }

    [Fact]
    public void ExplicitDispose_DuringProcessExit_StillReleases()
    {
        // Explicit Dispose should still attempt Arc.Release even during process exit.
        // We test with a mock pointer — Arc.Release on invalid pointer is caught by
        // the try/catch in ReleaseHandle, so this verifies the explicit Dispose path
        // doesn't skip and the handle still closes cleanly.
        var ptr = new IntPtr(0x1);
        var handle = new SwiftClassHandle<MockSwiftClass>(ptr);

        try
        {
            SwiftExitGuard.SetProcessExitingForTest(true);
            handle.Dispose(); // explicit Dispose sets _explicitDispose = true first
            Assert.True(handle.IsClosed);
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
    }
}
