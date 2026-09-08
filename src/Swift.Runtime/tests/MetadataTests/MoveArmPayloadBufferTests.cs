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
/// A callback Move wrapper owns an independent copied value and its container. Recording VWT
/// copy/destroy witnesses prove one retain and one destroy while Swift's source stays intact.
/// </summary>
public unsafe class MoveArmPayloadBufferTests
{
    private static int _destroyCount;
    private static int _copyCount;

    [UnmanagedCallersOnly]
    private static void* CountingCopy(void* destination, void* source, TypeMetadata metadata)
    {
        System.Buffer.MemoryCopy(source, destination, 16, 16);
        Interlocked.Increment(ref _copyCount);
        return destination;
    }

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
        vwt->InitializeWithCopy = &CountingCopy;
        vwt->Flags = ValueWitnessFlags.IsNonPOD;
        vwt->Size = 16;
        vwt->Stride = 16;
        var block = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
        block[0] = (IntPtr)vwt;
        block[1] = (IntPtr)0x200; // TypeMetadataKind.Struct
        return TypeMetadata.FromHandle((IntPtr)(block + 1));
    }

    /// <summary>
    /// Mirrors SwiftString: construction moves words into its own allocation. The callback reader
    /// must provide a copied +1 so normal owning cleanup remains armed.
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
    public void MarshalCallbackArg_MoveBufferShape_DisposeDestroysIndependentCopy()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveBufferFake), h => MoveBufferFake.NewFromPayload(h));
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);
            int copiesBefore = Volatile.Read(ref _copyCount);

            var wrapper = SwiftMarshal.MarshalCallbackArg<MoveBufferFake>(source);
            Assert.NotNull(wrapper);
            var payload = wrapper.Payload;

            Assert.NotEqual(source, payload.DangerousGetHandle());
            // Dispose destroys only the independent copy, leaving the borrowed source intact.
            wrapper.Dispose();

            Assert.True(payload.IsClosed);
            Assert.Equal(before + 1, Volatile.Read(ref _destroyCount));
            Assert.Equal(copiesBefore + 1, Volatile.Read(ref _copyCount));
            Assert.Equal(0x1122334455667788, *(long*)source);
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
            int copiesBefore = Volatile.Read(ref _copyCount);
            var wrapper = SwiftMarshal.MarshalCallbackArg<MoveBufferFake>(source);

            // Double dispose must destroy the independently owned copy exactly once.
            wrapper.Dispose();
            wrapper.Dispose();

            Assert.Equal(before + 1, Volatile.Read(ref _destroyCount));
            Assert.Equal(copiesBefore + 1, Volatile.Read(ref _copyCount));
            Assert.Equal(0x1122334455667788, *(long*)source);
        }
        finally
        {
            NativeMemory.Free((void*)source);
        }
    }

    [Fact]
    public void MarshalCallbackArg_MoveBufferShape_FinalizerDestroysIndependentCopy()
    {
        NewFromPayloadDispatcher.Register(typeof(MoveBufferFake), h => MoveBufferFake.NewFromPayload(h));
        var source = AllocSourceWords();
        try
        {
            int before = Volatile.Read(ref _destroyCount);
            int copiesBefore = Volatile.Read(ref _copyCount);

            CreateAndDropWrapper(source);
            for (int i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Finalization must release the copied value exactly once, without touching the source.
            Assert.Equal(before + 1, Volatile.Read(ref _destroyCount));
            Assert.Equal(copiesBefore + 1, Volatile.Read(ref _copyCount));
            Assert.Equal(0x1122334455667788, *(long*)source);
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
            int copiesBefore = Volatile.Read(ref _copyCount);

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

        // An explicitly caller-managed borrowed container is not moved out. The caller must keep
        // its source alive; the IsConsumed guard must not treat this as consumption.
        handle.MarkContentsBorrowed();

        Assert.False(handle.IsConsumed);
        handle.Dispose();
        Assert.True(handle.IsClosed);
    }
}
