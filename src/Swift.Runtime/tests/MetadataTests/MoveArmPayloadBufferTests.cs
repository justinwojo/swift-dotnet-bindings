// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavioral accounting for the borrowed-callback Move arm's two-part ownership shape (the
/// <c>SwiftString</c> shape): the wrapper's from-handle ctor bitwise-copies the borrowed (+0)
/// value words into a container buffer the WRAPPER allocates itself. The wrapper therefore owns
/// the container allocation — it must be freed exactly once — but NOT the value inside it, which
/// Swift still owns. The old blanket finalizer suppression foreclosed the container free (a leak
/// of the wrapper's own allocation per callback invocation), and an explicit Dispose ran the
/// value-witness Destroy on the borrowed value (an over-release).
///
/// These tests observe the value-witness Destroy DIRECTLY: the fake's metadata is a hand-built
/// native metadata block whose value witness table routes <c>Destroy</c> to a managed
/// <c>[UnmanagedCallersOnly]</c> counter. That makes both failure modes measurable on the desktop
/// host with no Swift runtime involved:
/// <list type="bullet">
/// <item>over-release: Destroy count must stay 0 for a callback-marshalled (borrowed) wrapper
/// across Dispose AND finalization;</item>
/// <item>leak: the container free must still run — the payload SafeHandle must close on Dispose
/// rather than being finalizer-suppressed into oblivion;</item>
/// <item>no behavior change for owned instances: a directly-constructed wrapper's Dispose still
/// runs Destroy exactly once.</item>
/// </list>
/// </summary>
public unsafe class MoveArmPayloadBufferTests
{
    private static int _destroyCount;

    [UnmanagedCallersOnly]
    private static void CountingDestroy(void* value, TypeMetadata metadata)
    {
        Interlocked.Increment(ref _destroyCount);
    }

    /// <summary>
    /// Hand-built metadata: [vwt pointer][kind word]. The handle points at the kind word;
    /// the VWT lives at handle[-1] per the Swift ABI. Kind = Struct (0x200), which keeps
    /// MarshalCallbackArg off the class fast path. Alive for the process lifetime.
    /// </summary>
    private static readonly TypeMetadata CraftedMetadata = CreateCraftedMetadata();

    private static TypeMetadata CreateCraftedMetadata()
    {
        var vwt = (ValueWitnessTable*)NativeMemory.AllocZeroed(512);
        vwt->Destroy = &CountingDestroy;
        vwt->Size = 16;
        vwt->Stride = 16;
        var block = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
        block[0] = (IntPtr)vwt;
        block[1] = (IntPtr)0x200; // TypeMetadataKind.Struct
        return TypeMetadata.FromHandle((IntPtr)(block + 1));
    }

    /// <summary>
    /// Mirrors SwiftString's Move shape exactly: from-handle ctor allocates its OWN 16-byte
    /// container and bitwise-copies the source words; declared Move semantics; consume marks the
    /// contents borrowed so cleanup frees the container without the value-witness Destroy.
    /// </summary>
    private sealed class MoveBufferFake : ISwiftObject
    {
        private readonly SwiftSafeHandle<MoveBufferFake> _payload;

        private MoveBufferFake(IntPtr source)
        {
            var buffer = (IntPtr)NativeMemory.Alloc(16);
            System.Buffer.MemoryCopy((void*)source, (void*)buffer, 16, 16);
            _payload = new SwiftSafeHandle<MoveBufferFake>(buffer);
        }

        public SwiftSafeHandle<MoveBufferFake> Payload => _payload;

        public void Dispose() => _payload.Dispose();
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => CraftedMetadata;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new MoveBufferFake(payload);
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Move;

        void ISwiftObject.SuppressPayloadFinalizer() => GC.SuppressFinalize(_payload);
        void ISwiftObject.ConsumePayloadBuffer() => _payload.MarkContentsBorrowed();
    }

    private static IntPtr AllocSourceWords()
    {
        // Stand-in for the borrowed two-word value the callback trampoline hands over.
        var source = (IntPtr)NativeMemory.AllocZeroed(16);
        *(long*)source = 0x1122334455667788;
        return source;
    }

    [Fact]
    public void MarshalCallbackArg_MoveBufferShape_DisposeFreesContainerWithoutDestroy()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveBufferFake), h => MoveBufferFake.NewFromPayload(h));
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);

            var wrapper = SwiftMarshal.MarshalCallbackArg<MoveBufferFake>(source);
            Assert.NotNull(wrapper);
            var payload = wrapper.Payload;

            // Dispose must free the wrapper-owned container (handle closes) WITHOUT running the
            // value-witness Destroy on the borrowed value Swift still owns (over-release class).
            wrapper.Dispose();

            Assert.True(payload.IsClosed);
            Assert.Equal(before, Volatile.Read(ref _destroyCount));
        }
        finally
        {
            NativeMemory.Free((void*)source);
        }
    }

    [Fact]
    public void MarshalCallbackArg_MoveBufferShape_DoubleDisposeIsSafe()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveBufferFake), h => MoveBufferFake.NewFromPayload(h));
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);
            var wrapper = SwiftMarshal.MarshalCallbackArg<MoveBufferFake>(source);

            // The SafeHandle owns the single free; disposing twice must neither double-free the
            // container (a crash class) nor run the Destroy.
            wrapper.Dispose();
            wrapper.Dispose();

            Assert.Equal(before, Volatile.Read(ref _destroyCount));
        }
        finally
        {
            NativeMemory.Free((void*)source);
        }
    }

    [Fact]
    public void MarshalCallbackArg_MoveBufferShape_FinalizerPathDoesNotDestroyBorrowedValue()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveBufferFake), h => MoveBufferFake.NewFromPayload(h));
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);

            CreateAndDropWrapper(source);
            for (int i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // The payload finalizer now RUNS (it is no longer suppressed, so the container is
            // reclaimable) — and its borrowed-contents path must skip the value-witness Destroy.
            Assert.Equal(before, Volatile.Read(ref _destroyCount));
        }
        finally
        {
            NativeMemory.Free((void*)source);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CreateAndDropWrapper(IntPtr source)
    {
        var wrapper = SwiftMarshal.MarshalCallbackArg<MoveBufferFake>(source);
        Assert.NotNull(wrapper);
    }

    [Fact]
    public void OwnedInstance_DisposeStillRunsDestroyExactlyOnce()
    {
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);

            // NOT marshalled through the borrowed-callback seam: an owned instance keeps the
            // normal cleanup — Dispose runs the value-witness Destroy exactly once, then frees.
            var owned = (MoveBufferFake)MoveBufferFake.NewFromPayload(source);
            owned.Dispose();

            Assert.Equal(before + 1, Volatile.Read(ref _destroyCount));
        }
        finally
        {
            NativeMemory.Free((void*)source);
        }
    }

    [Fact]
    public void MarkContentsBorrowed_DoesNotFlagValueAsConsumed()
    {
        var buffer = (IntPtr)NativeMemory.Alloc(16);
        var handle = new SwiftSafeHandle<MoveBufferFake>(buffer);

        // Borrowed contents remain READABLE for the wrapper's lifetime — the use-after-move guard
        // (IsConsumed) must not trip for a borrowed-callback wrapper.
        handle.MarkContentsBorrowed();

        Assert.False(handle.IsConsumed);
        handle.Dispose();
        Assert.True(handle.IsClosed);
    }
}
