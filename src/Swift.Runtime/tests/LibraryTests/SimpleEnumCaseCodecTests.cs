// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// A simple enum with a registered case codec (<see cref="SimpleEnumCaseCodecs"/>) moves through
/// Swift memory only through that codec: its C# constant is not the in-memory tag. The fake codec
/// here stands in for the binding's wrapper functions, using <c>Int32</c>'s metadata (so
/// <c>Optional</c> is Swift's five-byte <c>Optional&lt;Int32&gt;</c>) and storing each case as its
/// constant plus <see cref="TagShift"/>, so a site that copied the constant as bytes reads the
/// wrong value.
/// </summary>
public class SimpleEnumCaseCodecTests
{
    private const int TagShift = 10;

    private enum Coded : int
    {
        First = 0,
        Second = 1,
    }

    private enum Unsized : int
    {
        Only = 0,
    }

    private sealed unsafe class ShiftedCodec : ISimpleEnumCaseCodec
    {
        public long Load(IntPtr source) => *(int*)source - TagShift;

        public bool Store(long value, IntPtr destination)
        {
            if (value is < 0 or > 1)
                return false;
            *(int*)destination = (int)value + TagShift;
            return true;
        }

        public bool LoadOptional(IntPtr source, out long value)
        {
            value = 0;
            if (((byte*)source)[4] != 0)
                return false;
            value = Load(source);
            return true;
        }

        public bool StoreOptional(bool hasValue, long value, IntPtr destination)
        {
            if (!hasValue)
            {
                *(int*)destination = 0;
                ((byte*)destination)[4] = 1;
                return true;
            }
            if (!Store(value, destination))
                return false;
            ((byte*)destination)[4] = 0;
            return true;
        }
    }

    static SimpleEnumCaseCodecTests()
    {
        if (TypeMetadata.TryGetTypeMetadata<int>(out var intMd))
            TypeMetadata.RegisterMetadata(typeof(Coded), intMd.Value);
        SimpleEnumCaseCodecs.Register(typeof(Coded), new ShiftedCodec());
        SimpleEnumCaseCodecs.Register(typeof(Unsized), new ShiftedCodec());
    }

    [Fact]
    public void Register_RefusesANonEnumAndKeepsTheFirstCodec()
    {
        Assert.Throws<ArgumentException>(() => SimpleEnumCaseCodecs.Register(typeof(int), new ShiftedCodec()));
        var first = SimpleEnumCaseCodecs.TryGet(typeof(Coded), out var codec) ? codec : null;
        SimpleEnumCaseCodecs.Register(typeof(Coded), new ShiftedCodec());
        Assert.True(SimpleEnumCaseCodecs.TryGet(typeof(Coded), out var again));
        Assert.Same(first, again);
    }

    [Fact]
    public unsafe void OptionalSome_WritesTheCaseThroughTheCodec()
    {
        using var optional = SwiftOptional<Coded>.NewSome(Coded.Second);
        var bytes = (byte*)optional.Payload.DangerousGetHandle();
        Assert.Equal(1 + TagShift, *(int*)bytes);
        Assert.Equal(0, bytes[4]);
        Assert.True(optional.HasValue);
        Assert.Equal(Coded.Second, optional.Some);
    }

    [Fact]
    public unsafe void OptionalNone_IsSwiftsNil()
    {
        using var optional = SwiftOptional<Coded>.NewNone();
        Assert.Equal(1, ((byte*)optional.Payload.DangerousGetHandle())[4]);
        Assert.False(optional.HasValue);
        Assert.Equal(SwiftOptionalCases.None, optional.Case);
    }

    [Fact]
    public void OptionalSome_RefusesAValueThatNamesNoCase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftOptional<Coded>.NewSome((Coded)2));
    }

    [Fact]
    public unsafe void OptionalReadFromSwiftMemory_DecodesThroughTheCodec()
    {
        var memory = (byte*)NativeMemory.AllocZeroed(8);
        try
        {
            *(int*)memory = 0 + TagShift;
            using var optional = SwiftMarshal.MarshalFromSwift<SwiftOptional<Coded>>((IntPtr)memory);
            Assert.Equal(Coded.First, optional.Some);

            memory[4] = 1;
            using var none = SwiftMarshal.MarshalFromSwift<SwiftOptional<Coded>>((IntPtr)memory);
            Assert.False(none.HasValue);
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [Fact]
    public unsafe void EnumValueInSwiftMemory_CrossesThroughTheCodecBothWays()
    {
        var memory = (byte*)NativeMemory.AllocZeroed(8);
        try
        {
            var span = new Span<byte>(memory, 8);
            SwiftMarshal.MarshalToSwift(Coded.Second, ref span);
            Assert.Equal(1 + TagShift, *(int*)memory);
            Assert.Equal(Coded.Second, SwiftMarshal.MarshalFromSwift<Coded>((IntPtr)memory));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var again = new Span<byte>(memory, 8);
                SwiftMarshal.MarshalToSwift((Coded)7, ref again);
            });
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [Fact]
    public unsafe void EnumValueWithACodecButNoMetadata_IsRefusedBeforeAnyWrite()
    {
        var memory = (byte*)NativeMemory.AllocZeroed(1);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                var span = new Span<byte>(memory, 1);
                SwiftMarshal.MarshalToSwift(Unsized.Only, ref span);
            });
            Assert.Equal(0, memory[0]);
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }
}
