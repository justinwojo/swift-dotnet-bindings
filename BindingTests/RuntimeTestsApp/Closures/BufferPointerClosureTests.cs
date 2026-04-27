// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests for non-escaping buffer-pointer closure parameters — the
/// <c>withUnsafeBytes(_:)</c> / <c>withUnsafeMutableBytes(_:)</c> shape that
/// drives RealityFoundation.LowLevelBuffer. The Swift wrapper has to split the
/// 16-byte UnsafeRawBufferPointer into <c>(baseAddress, count)</c> at the
/// @convention(c) callback boundary so the C# delegate can reconstruct it.
/// </summary>
public class BufferPointerClosureTests : TestBase
{
    public BufferPointerClosureTests(TestResults results) : base(results) { }

    public void TestWithUnsafeBytesReadsPayload()
    {
        var provider = new BufferProvider(new byte[] { 0x10, 0x20, 0x30, 0x40 });
        byte[]? observed = null;
        provider.WithUnsafeBytes(buf =>
        {
            unsafe
            {
                observed = new byte[(int)buf.Count];
                var src = (byte*)buf.BaseAddress;
                for (int i = 0; i < observed.Length; i++)
                    observed[i] = src[i];
            }
        });
        AssertNotNull(observed, "withUnsafeBytes invoked");
        AssertEqual(4, observed!.Length, "buffer length = 4");
        AssertEqual((byte)0x10, observed[0], "byte[0] preserved");
        AssertEqual((byte)0x20, observed[1], "byte[1] preserved");
        AssertEqual((byte)0x30, observed[2], "byte[2] preserved");
        AssertEqual((byte)0x40, observed[3], "byte[3] preserved");
        TestLogger.Info("BufferProvider.WithUnsafeBytes round-trip passed");
    }

    public void TestWithUnsafeMutableBytesWritesObservable()
    {
        var provider = new BufferProvider(new byte[] { 0, 0, 0, 0 });
        provider.WithUnsafeMutableBytes(buf =>
        {
            unsafe
            {
                AssertEqual(4, (int)buf.Count, "mutable buffer length = 4");
                var dst = (byte*)buf.BaseAddress;
                dst[0] = 0xAA;
                dst[1] = 0xBB;
                dst[2] = 0xCC;
                dst[3] = 0xDD;
            }
        });
        AssertEqual((byte)0xAA, provider._byte(0), "byte[0] mutated");
        AssertEqual((byte)0xBB, provider._byte(1), "byte[1] mutated");
        AssertEqual((byte)0xCC, provider._byte(2), "byte[2] mutated");
        AssertEqual((byte)0xDD, provider._byte(3), "byte[3] mutated");
        TestLogger.Info("BufferProvider.WithUnsafeMutableBytes round-trip passed");
    }

    public void TestWithUnsafeBytesEmptyBuffer()
    {
        var provider = new BufferProvider(new byte[0]);
        var invoked = false;
        long observedCount = -1;
        provider.WithUnsafeBytes(buf =>
        {
            invoked = true;
            observedCount = buf.Count;
        });
        AssertTrue(invoked, "withUnsafeBytes invoked on empty buffer");
        AssertEqual(0L, observedCount, "empty buffer count = 0");
        TestLogger.Info("BufferProvider.WithUnsafeBytes empty-buffer passed");
    }

    public void TestWithUnsafeBytesThrowingReadsPayload()
    {
        var provider = new BufferProvider(new byte[] { 0x11, 0x22, 0x33 });
        long observedLen = -1;
        byte first = 0;
        provider.WithUnsafeBytesThrowing(buf =>
        {
            unsafe
            {
                observedLen = buf.Count;
                first = ((byte*)buf.BaseAddress)[0];
            }
        });
        AssertEqual(3L, observedLen, "throwing buffer length = 3");
        AssertEqual((byte)0x11, first, "throwing byte[0] preserved");
        TestLogger.Info("BufferProvider.WithUnsafeBytesThrowing round-trip passed");
    }

    public void TestWithUnsafeBytesThrowingClosureFailurePropagates()
    {
        // Regression for the void-return throwing-callback fall-through bug:
        // the failure branch must NOT fall into the success block and clear
        // *errorOut to default after writing it. With the bug the closure's
        // SwiftResult.FromFailure(error) is silently swallowed; with the fix
        // the error surfaces back as a SwiftException.
        var provider = new BufferProvider(new byte[] { 0x11, 0x22, 0x33 });
        var errorPtr = (IntPtr)BufferProvider.MakeRetainedTestErrorPtr();
        AssertTrue(errorPtr != IntPtr.Zero, "test error pointer is non-zero");

        SwiftError swiftError;
        unsafe { swiftError = new SwiftError(errorPtr.ToPointer()); }

        bool caught = false;
        string? message = null;
        try
        {
            provider.WithUnsafeBytesThrowing(buf =>
                Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromFailure(swiftError));
        }
        catch (SwiftException ex)
        {
            caught = true;
            message = ex.Message;
        }
        AssertTrue(caught, "closure FromFailure surfaces as SwiftException");
        AssertTrue(message?.Contains("forced") ?? false,
            $"SwiftException message describes BufferProviderError.forced; got: {message}");
        TestLogger.Info("BufferProvider.WithUnsafeBytesThrowing failure-path passed");
    }

    public void TestWithUnsafeBytesIndirectReturnPropagatesStruct()
    {
        var provider = new BufferProvider(new byte[] { 1, 2, 3, 4 });
        var result = provider.WithUnsafeBytesIndirectReturn(buf =>
        {
            unsafe
            {
                int sum = 0;
                var src = (byte*)buf.BaseAddress;
                for (int i = 0; i < (int)buf.Count; i++)
                    sum += src[i];
                return new NonFrozenPoint((double)buf.Count, (double)sum);
            }
        });
        AssertEqual(4.0, result.X, "indirect-return X = buffer count");
        AssertEqual(10.0, result.Y, "indirect-return Y = byte sum");
        TestLogger.Info("BufferProvider.WithUnsafeBytesIndirectReturn round-trip passed");
    }
}
