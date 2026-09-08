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
}
