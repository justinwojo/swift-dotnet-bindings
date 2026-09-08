// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public unsafe class BorrowedPodSlotTests
{
    [Theory]
    [InlineData("callback")]
    [InlineData("callback-slot")]
    [InlineData("borrowed-slot")]
    public void BorrowedPodAdopt_DisposeAndCaptureDoNotOwnSource(string route)
    {
        SwiftMarshal.RegisterSwiftObjectFactory<PodValue>();
        SwiftMarshal.RegisterPayloadSemantics(typeof(PodValue), PayloadConstructionSemantics.Adopt);
        int* source = (int*)NativeMemory.Alloc((nuint)sizeof(int));
        *source = 73;
        PodValue? value = null;
        bool sourceAdopted = false;
        try
        {
            value = route switch
            {
                "callback" => SwiftMarshal.MarshalCallbackArg<PodValue>((IntPtr)source),
                "callback-slot" => SwiftMarshal.MarshalCallbackArgFromSlot<PodValue>((IntPtr)source),
                _ => SwiftMarshal.MarshalBorrowedValueFromSlot<PodValue>(source, PodValue.GetTypeMetadata())
            };
            sourceAdopted = value.SwiftHandle == (IntPtr)source;
            Assert.False(sourceAdopted);
            *source = 91; // Native frame may reuse its storage after callback return.
            Assert.Equal(73, value.Value);
            value.Dispose();
            Assert.Equal(91, *source); // Explicit Dispose never frees or writes the borrowed slot.
        }
        finally
        {
            value?.Dispose();
            if (!sourceAdopted) NativeMemory.Free(source); // Also safe against the old aliasing reader.
        }
    }

    private static readonly TypeMetadata Metadata = CreateMetadata();
    private static TypeMetadata CreateMetadata()
    {
        var vwt = (ValueWitnessTable*)NativeMemory.AllocZeroed(512);
        vwt->Size = sizeof(int);
        vwt->Stride = sizeof(int);
        var metadata = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * IntPtr.Size));
        metadata[0] = (IntPtr)vwt;
        metadata[1] = (IntPtr)0x200;
        return TypeMetadata.FromHandle((IntPtr)(metadata + 1));
    }

    public sealed class PodValue : ISwiftStruct
    {
        private IntPtr _payload;
        private PodValue(IntPtr payload) => _payload = payload;
        public IntPtr SwiftHandle => _payload;
        public int Value => *(int*)_payload;
        public static TypeMetadata GetTypeMetadata() => Metadata;
        public static PayloadConstructionSemantics PayloadConstructionSemantics => PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new PodValue(payload);
        public int MarshalToSwift(ref Span<byte> destination) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<T>() where T : class => throw new NotSupportedException();
        public void Dispose()
        {
            NativeMemory.Free((void*)_payload);
            _payload = IntPtr.Zero;
        }
    }
}
