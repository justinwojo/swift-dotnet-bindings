// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Host-side checks on the runtime's <see cref="DispatchQueue"/> projection: the main and global
/// queues resolve through libdispatch's C surface, the wrapper's handle is the queue object pointer
/// (the class convention generated callers marshal by), and disposal is safe and final.
/// </summary>
public class DispatchQueueTests
{
    [Fact]
    public void Main_ResolvesTheMainQueue()
    {
        using var main = DispatchQueue.Main;
        Assert.Equal("com.apple.main-thread", main.Label);
    }

    [Fact]
    public void Main_IsASingleton()
    {
        using var first = DispatchQueue.Main;
        using var second = DispatchQueue.Main;
        Assert.Equal(first.Payload.DangerousGetHandle(), second.Payload.DangerousGetHandle());
    }

    [Fact]
    public void Global_DefaultMatchesSwiftsDefaultQos()
    {
        using var global = DispatchQueue.Global();
        using var explicitDefault = DispatchQueue.Global(DispatchQoSClass.Default);
        Assert.Equal("com.apple.root.default-qos", global.Label);
        Assert.Equal(explicitDefault.Payload.DangerousGetHandle(), global.Payload.DangerousGetHandle());
    }

    [Theory]
    [InlineData(DispatchQoSClass.Background, "com.apple.root.background-qos")]
    [InlineData(DispatchQoSClass.Utility, "com.apple.root.utility-qos")]
    [InlineData(DispatchQoSClass.Default, "com.apple.root.default-qos")]
    [InlineData(DispatchQoSClass.UserInitiated, "com.apple.root.user-initiated-qos")]
    [InlineData(DispatchQoSClass.UserInteractive, "com.apple.root.user-interactive-qos")]
    public void Global_ResolvesTheQueueForEachQosClass(DispatchQoSClass qos, string expectedLabel)
    {
        using var queue = DispatchQueue.Global(qos);
        Assert.Equal(expectedLabel, queue.Label);
    }

    [Fact]
    public void Handle_IsTheQueueObjectPointer()
    {
        using var main = DispatchQueue.Main;
        var handle = main.Payload.DangerousGetHandle();
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(handle, ((ISwiftObject)main).SwiftHandle);
    }

    [Fact]
    public void MarshalToSwift_WritesTheObjectPointerByClassConvention()
    {
        using var main = DispatchQueue.Main;
        var metadata = SwiftObjectHelper<DispatchQueue>.GetTypeMetadata();
        Assert.Equal((nuint)IntPtr.Size, metadata.Size);

        Span<byte> destination = stackalloc byte[IntPtr.Size];
        int written = ((ISwiftObject)main).MarshalToSwift(ref destination);

        Assert.Equal(IntPtr.Size, written);
        var copied = System.Runtime.InteropServices.MemoryMarshal.Read<IntPtr>(destination);
        Assert.Equal(main.Payload.DangerousGetHandle(), copied);
        // Balance the copy's retain (a no-op on the immortal main queue, but the contract is +1).
        Arc.UnknownObjectRelease(copied);
    }

    [Fact]
    public void Dispose_IsIdempotentAndRejectsFurtherUse()
    {
        var queue = DispatchQueue.Global(DispatchQoSClass.Utility);
        queue.Dispose();
        queue.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.Label);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            Span<byte> destination = stackalloc byte[IntPtr.Size];
            ((ISwiftObject)queue).MarshalToSwift(ref destination);
        });
    }

    [Fact]
    public void Finalizer_ReleasesUndisposedWrappersWithoutFault()
    {
        for (int i = 0; i < 16; i++)
        {
            var queue = DispatchQueue.Global(DispatchQoSClass.Background);
            Assert.NotEmpty(queue.Label);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void Payload_KeepsCustomQueueAliveAfterWrapperCollection()
    {
        var (payload, wrapper, id) = CreateQueuePayload();
        try
        {
            CollectWrappers();
            Assert.False(wrapper.IsAlive);
            Assert.False(payload.IsClosed);
            Assert.Equal(0, FinalizationCount(id));
        }
        finally
        {
            payload.Dispose();
        }
        AssertFinalizedOnce(id);
    }

    [Fact]
    public void Dispose_DefersNativeReleaseUntilLastPayloadPinLeaves()
    {
        var (raw, id) = CreateTrackedQueue();
        var queue = SwiftMarshal.MarshalFromSwiftObject<DispatchQueue>(raw);
        var payload = queue.Payload;
        bool pinned = false;
        payload.DangerousAddRef(ref pinned);
        try
        {
            queue.Dispose();
            queue.Dispose();
            Assert.Equal(0, FinalizationCount(id));
            Assert.Throws<ObjectDisposedException>(() => queue.Label);
            Assert.Throws<ObjectDisposedException>(() => queue.Payload);
        }
        finally
        {
            if (pinned) payload.DangerousRelease();
            queue.Dispose();
        }
        AssertFinalizedOnce(id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedQueue_NeverReleasesTheNativeOwnersReference(bool dispose)
    {
        var (raw, id) = CreateTrackedQueue();
        var wrapper = WrapBorrowedQueue(raw, dispose);
        try
        {
            CollectWrappers();
            Assert.False(wrapper.IsAlive);
            Assert.Equal(0, FinalizationCount(id));
        }
        finally
        {
            Arc.UnknownObjectRelease(raw);
        }
        AssertFinalizedOnce(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SwiftSafeHandle<DispatchQueue> Payload, WeakReference Wrapper, nint Id) CreateQueuePayload()
    {
        var (raw, id) = CreateTrackedQueue();
        var queue = SwiftMarshal.MarshalFromSwiftObject<DispatchQueue>(raw);
        var payload = queue.Payload;
        return (payload, new WeakReference(queue), id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WrapBorrowedQueue(IntPtr raw, bool dispose)
    {
        var queue = SwiftMarshal.MarshalFromSwiftObject<DispatchQueue>(raw);
        ((ISwiftObject)queue).SuppressPayloadFinalizer();
        if (dispose) queue.Dispose();
        return new WeakReference(queue);
    }

    private static void CollectWrappers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // A native dispatch finalizer reports actual object destruction. Tests never dereference a
    // potentially freed pointer, and each queue has a distinct key so xUnit concurrency is safe.
    private static readonly ConcurrentDictionary<nint, int> FinalizationCounts = new();
    private static int _nextQueueId;

    private static unsafe (IntPtr Queue, nint Id) CreateTrackedQueue()
    {
        nint id = Interlocked.Increment(ref _nextQueueId);
        FinalizationCounts[id] = 0;
        var queue = dispatch_queue_create("com.swiftbindings.tests.pinned-payload", IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, queue);
        dispatch_set_context(queue, id);
        dispatch_set_finalizer_f(queue, &OnQueueFinalized);
        return (queue, id);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnQueueFinalized(nint context)
        => FinalizationCounts.AddOrUpdate(context, 1, static (_, count) => count + 1);

    private static int FinalizationCount(nint id) => FinalizationCounts[id];

    private static void AssertFinalizedOnce(nint id)
    {
        Assert.True(SpinWait.SpinUntil(() => FinalizationCount(id) != 0, TimeSpan.FromSeconds(5)),
            "custom queue must be destroyed after its last owning handle is released");
        Assert.Equal(1, FinalizationCount(id));
    }

    [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr attributes);
    [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dispatch_set_context(IntPtr queue, nint context);
    [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void dispatch_set_finalizer_f(IntPtr queue, delegate* unmanaged[Cdecl]<nint, void> finalizer);

}
