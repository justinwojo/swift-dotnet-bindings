// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public unsafe class MovedSlotPodAdoptTests
{
    private static int _bareCopies;
    private static int _bareDestroys;

    [UnmanagedCallersOnly]
    private static void* CopyBareNonPod(void* destination, void* source, TypeMetadata metadata)
    {
        _bareCopies++;
        System.Buffer.MemoryCopy(source, destination, (long)metadata.Size, (long)metadata.Size);
        return destination;
    }

    [UnmanagedCallersOnly]
    private static void DestroyBareNonPod(void* value, TypeMetadata metadata) => _bareDestroys++;

    [Fact]
    public void BareNonPodAdopt_MovedSlotCopiesThenConsumesSource()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<BareNonPodAdoptValue>();
        SwiftMarshal.RegisterPayloadSemantics(
            typeof(BareNonPodAdoptValue), PayloadConstructionSemantics.Adopt);
        var metadata = BareNonPodAdoptValue.GetTypeMetadata();
        _bareCopies = 0;
        _bareDestroys = 0;

        int* slot = (int*)NativeMemory.Alloc(metadata.Size);
        *slot = 73;
        BareNonPodAdoptValue? result = null;
        try
        {
            result = SwiftMarshal.MarshalMovedValueFromSlot<BareNonPodAdoptValue>(slot, metadata);

            Assert.NotEqual((IntPtr)slot, result.SwiftHandle);
            Assert.Equal(1, _bareCopies);
            Assert.Equal(1, _bareDestroys); // the owned source slot was consumed
            NativeMemory.Free(slot);
            slot = null;
            Assert.Equal(73, result.Value);
        }
        finally
        {
            if (slot != null)
            {
                metadata.ValueWitnessTable->Destroy(slot, metadata);
                NativeMemory.Free(slot);
            }
            result?.Dispose();
        }

        Assert.Equal(2, _bareDestroys); // independent wrapper copy disposed
    }

    [Fact]
    public void BareNonPodAdopt_CopiedSlotLeavesBorrowedSourceIntact()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<BareNonPodAdoptValue>();
        SwiftMarshal.RegisterPayloadSemantics(
            typeof(BareNonPodAdoptValue), PayloadConstructionSemantics.Adopt);
        var metadata = BareNonPodAdoptValue.GetTypeMetadata();
        _bareCopies = 0;
        _bareDestroys = 0;

        int* slot = (int*)NativeMemory.Alloc(metadata.Size);
        *slot = 81;
        BareNonPodAdoptValue? result = null;
        try
        {
            result = SwiftMarshal.MarshalCopiedValueFromSlot<BareNonPodAdoptValue>((IntPtr)slot);

            Assert.NotEqual((IntPtr)slot, result.SwiftHandle);
            Assert.Equal(1, _bareCopies);
            Assert.Equal(0, _bareDestroys); // the range/carrier still owns its borrowed slot
            *slot = 99;
            Assert.Equal(81, result.Value);
        }
        finally
        {
            metadata.ValueWitnessTable->Destroy(slot, metadata);
            NativeMemory.Free(slot);
            result?.Dispose();
        }

        Assert.Equal(2, _bareDestroys); // borrowed source plus independent wrapper copy
    }

    [Fact]
    public void PodAdopt_ResultOwnsIndependentStorageSoCallerCanFreeSlot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        SwiftMarshal.RegisterSwiftObjectFactory<PodAdoptValue>();
        SwiftMarshal.RegisterPayloadSemantics(typeof(PodAdoptValue), PayloadConstructionSemantics.Adopt);
        var metadata = PodAdoptValue.GetTypeMetadata();
        Assert.False(metadata.ValueWitnessTable->IsNonPOD);
        int* slot = (int*)NativeMemory.Alloc((nuint)sizeof(int));
        *slot = 73;
        PodAdoptValue? result = null;
        try
        {
            result = SwiftMarshal.MarshalMovedValueFromSlot<PodAdoptValue>(slot, metadata);
            Assert.NotEqual((IntPtr)slot, result.SwiftHandle);
            NativeMemory.Free(slot);
            slot = null;
            Assert.Equal(73, result.Value);
        }
        finally
        {
            // Keep this negative control safe on the old implementation, which adopts slot.
            if (slot != null && (result is null || result.SwiftHandle != (IntPtr)slot))
                NativeMemory.Free(slot);
            result?.Dispose();
        }
    }

    [Fact]
    public void EmptyPodAdopt_ResultOwnsNonNullIndependentStorage()
    {
        if (!OperatingSystem.IsMacOS()) return;
        SwiftMarshal.RegisterSwiftObjectFactory<EmptyPodAdoptValue>();
        SwiftMarshal.RegisterPayloadSemantics(typeof(EmptyPodAdoptValue), PayloadConstructionSemantics.Adopt);
        var metadata = EmptyPodAdoptValue.GetTypeMetadata();
        Assert.Equal((nuint)0, metadata.Size);
        Assert.False(metadata.ValueWitnessTable->IsNonPOD);
        void* slot = NativeMemory.Alloc(1);
        EmptyPodAdoptValue? result = null;
        try
        {
            result = SwiftMarshal.MarshalMovedValueFromSlot<EmptyPodAdoptValue>(slot, metadata);
            Assert.NotEqual(IntPtr.Zero, result.SwiftHandle);
            Assert.NotEqual((IntPtr)slot, result.SwiftHandle);
            NativeMemory.Free(slot);
            slot = null;
            Assert.True(result.WasConstructed);
        }
        finally
        {
            if (slot != null && (result is null || result.SwiftHandle != (IntPtr)slot))
                NativeMemory.Free(slot);
            result?.Dispose();
        }
    }

    public sealed class PodAdoptValue : ISwiftStruct
    {
        private IntPtr payload;
        private PodAdoptValue(IntPtr payload) => this.payload = payload;
        public int Value => *(int*)payload;
        public IntPtr SwiftHandle => payload;
        public static TypeMetadata GetTypeMetadata() => TypeMetadata.GetTypeMetadataOrThrow<int>();
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new PodAdoptValue(payload);
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
        public void Dispose()
        {
            NativeMemory.Free((void*)payload);
            payload = IntPtr.Zero;
        }
    }

    public sealed class EmptyPodAdoptValue : ISwiftStruct
    {
        private IntPtr payload;
        private EmptyPodAdoptValue(IntPtr payload) => this.payload = payload;
        public bool WasConstructed => payload != IntPtr.Zero;
        public IntPtr SwiftHandle => payload;
        public static TypeMetadata GetTypeMetadata()
        {
            // SwiftVoid resolves the canonical empty-tuple accessor metadata. typeof(void)
            // uses a legacy symbol cache entry and is not the generic unit-value authority.
            return TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftVoid>();
        }
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new EmptyPodAdoptValue(payload);
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class => throw new NotSupportedException();
        public void Dispose()
        {
            NativeMemory.Free((void*)payload);
            payload = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Models the hand-written SwiftUI value wrappers: a C# reference type implementing only
    /// ISwiftObject, backed by a non-POD Swift struct and adopting its payload allocation.
    /// </summary>
    public sealed class BareNonPodAdoptValue : ISwiftObject
    {
        private IntPtr _payload;
        private BareNonPodAdoptValue(IntPtr payload) => _payload = payload;
        public IntPtr SwiftHandle => _payload;
        public int Value => *(int*)_payload;
        public static TypeMetadata GetTypeMetadata() => BareNonPodMetadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics
            => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload)
            => new BareNonPodAdoptValue(payload);
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class => throw new NotSupportedException();
        public void Dispose()
        {
            if (_payload == IntPtr.Zero)
                return;
            BareNonPodMetadata.ValueWitnessTable->Destroy((void*)_payload, BareNonPodMetadata);
            NativeMemory.Free((void*)_payload);
            _payload = IntPtr.Zero;
        }
    }

    private static readonly TypeMetadata BareNonPodMetadata = CreateBareNonPodMetadata();

    private static TypeMetadata CreateBareNonPodMetadata()
    {
        var witnesses = (ValueWitnessTable*)NativeMemory.AllocZeroed(512);
        witnesses->InitializeWithCopy = &CopyBareNonPod;
        witnesses->Destroy = &DestroyBareNonPod;
        witnesses->Size = (nuint)sizeof(int);
        witnesses->Stride = (nuint)sizeof(int);
        witnesses->Flags = ValueWitnessFlags.IsNonPOD;

        var record = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * IntPtr.Size));
        record[0] = (IntPtr)witnesses;
        record[1] = (IntPtr)(long)TypeMetadataKind.Struct;
        return TypeMetadata.FromHandle((IntPtr)(record + 1));
    }
}
