// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public class TupleTests : IClassFixture<TupleTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public TupleTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }
    }

    #region ValueTuple Detection Tests

    [Fact]
    public void IsValueTupleType_ReturnsTrue_ForValueTuple2()
    {
        Assert.True(TypeMetadata.IsValueTupleType(typeof(ValueTuple<int, bool>)));
    }

    [Fact]
    public void IsValueTupleType_ReturnsTrue_ForValueTuple3()
    {
        Assert.True(TypeMetadata.IsValueTupleType(typeof(ValueTuple<int, bool, string>)));
    }

    [Fact]
    public void IsValueTupleType_ReturnsTrue_ForValueTuple7()
    {
        Assert.True(TypeMetadata.IsValueTupleType(typeof(ValueTuple<int, int, int, int, int, int, int>)));
    }

    [Fact]
    public void IsValueTupleType_ReturnsFalse_ForNonTuple()
    {
        Assert.False(TypeMetadata.IsValueTupleType(typeof(int)));
        Assert.False(TypeMetadata.IsValueTupleType(typeof(string)));
        Assert.False(TypeMetadata.IsValueTupleType(typeof(List<int>)));
    }

    [Fact]
    public void GetTupleElementTypes_ReturnsCorrectTypes()
    {
        var types = TypeMetadata.GetTupleElementTypes(typeof(ValueTuple<int, bool, double>));
        Assert.Equal(3, types.Length);
        Assert.Equal(typeof(int), types[0]);
        Assert.Equal(typeof(bool), types[1]);
        Assert.Equal(typeof(double), types[2]);
    }

    #endregion

    #region Tuple Metadata Tests

    /// <summary>
    /// A tuple's metadata must resolve from the <see cref="Type"/> alone, agreeing with the generic
    /// lookup. This is the shape reverse-dispatch receivers take when a Swift callback hands a tuple
    /// parameter across: the element types are only known as Types there, and resolving them by
    /// closing the generic lookup reflectively is unsupported on NativeAOT.
    /// </summary>
    [Fact]
    public void TryGetTypeMetadata_ByType_AgreesWithGenericLookup_ForTuple()
    {
        var byTypeSuccess = TypeMetadata.TryGetTypeMetadata(typeof((long, double)), out var byType, out var failure);
        var genericSuccess = TypeMetadata.TryGetTypeMetadata<(long, double)>(out var generic);

        Assert.Null(failure);
        Assert.True(byTypeSuccess);
        Assert.True(genericSuccess);
        Assert.Equal(TypeMetadataKind.Tuple, byType!.Value.Kind);
        Assert.Equal(generic!.Value, byType!.Value);
    }

    [Fact]
    public void TryGetTypeMetadata_ReturnsTupleMetadata_ForValueTuple2()
    {
        var success = TypeMetadata.TryGetTypeMetadata<(int, bool)>(out var metadata);

        Assert.True(success);
        Assert.NotNull(metadata);
        Assert.True(metadata.Value.IsValid);
        Assert.Equal(TypeMetadataKind.Tuple, metadata.Value.Kind);
    }

    [Fact]
    public void TryGetTypeMetadata_ReturnsTupleMetadata_ForValueTuple3()
    {
        var success = TypeMetadata.TryGetTypeMetadata<(int, long, double)>(out var metadata);

        Assert.True(success);
        Assert.NotNull(metadata);
        Assert.True(metadata.Value.IsValid);
        Assert.Equal(TypeMetadataKind.Tuple, metadata.Value.Kind);
    }

    [Fact]
    public void TryGetTypeMetadata_ReturnsTupleMetadata_ForValueTuple7()
    {
        var success = TypeMetadata.TryGetTypeMetadata<(int, int, int, int, int, int, int)>(out var metadata);

        Assert.True(success);
        Assert.NotNull(metadata);
        Assert.True(metadata.Value.IsValid);
        Assert.Equal(TypeMetadataKind.Tuple, metadata.Value.Kind);
    }

    [Fact]
    public void TupleMetadata_HasCorrectSize_ForIntBoolTuple()
    {
        var success = TypeMetadata.TryGetTypeMetadata<(int, bool)>(out var metadata);

        Assert.True(success);
        // Swift (Int32, Bool) should be 5 bytes with stride 8 due to alignment
        // Int32 is 4 bytes, Bool is 1 byte, total 5 bytes rounded up
        Assert.True(metadata!.Value.Size >= 5);
    }

    [Fact]
    public void TupleMetadata_HasCorrectSize_ForTwoLongs()
    {
        var success = TypeMetadata.TryGetTypeMetadata<(long, long)>(out var metadata);

        Assert.True(success);
        // Swift (Int64, Int64) should be 16 bytes
        Assert.Equal((nuint)16, metadata!.Value.Size);
    }

    [Fact]
    public void GetTupleTypeMetadataOrThrow_Works()
    {
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, double)>();

        Assert.True(metadata.IsValid);
        Assert.Equal(TypeMetadataKind.Tuple, metadata.Kind);
    }

    [Fact]
    public void GetTupleTypeMetadataOrThrow_ThrowsForNonTuple()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            TypeMetadata.GetTupleTypeMetadataOrThrow<int>();
        });
    }

    #endregion

    #region Tuple Marshalling Tests

    [Fact]
    public unsafe void MarshalToSwift_Works_ForSimpleTuple()
    {
        var tuple = (42, true);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, bool)>();
        var buffer = stackalloc byte[(int)metadata.Size];
        var span = new Span<byte>(buffer, (int)metadata.Size);

        var bytesWritten = SwiftMarshal.MarshalToSwift(tuple, ref span);

        Assert.True(bytesWritten > 0);
        Assert.True(bytesWritten <= (int)metadata.Size);
    }

    [Fact]
    public unsafe void MarshalToSwift_Works_ForLongTuple()
    {
        var tuple = (1, 2, 3, 4, 5);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, int, int, int, int)>();
        var buffer = stackalloc byte[(int)metadata.Size];
        var span = new Span<byte>(buffer, (int)metadata.Size);

        var bytesWritten = SwiftMarshal.MarshalToSwift(tuple, ref span);

        Assert.True(bytesWritten > 0);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForIntBoolTuple()
    {
        var original = (42, true);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, bool)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(int, bool)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForThreeInts()
    {
        var original = (10, 20, 30);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, int, int)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(int, int, int)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
        Assert.Equal(original.Item3, result.Item3);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForMixedTypes()
    {
        var original = (42, 3.14, true);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, double, bool)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(int, double, bool)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
        Assert.Equal(original.Item3, result.Item3);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForLongValues()
    {
        var original = (long.MaxValue, long.MinValue);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(long, long)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(long, long)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForFloatDouble()
    {
        var original = (1.5f, 2.5);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(float, double)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(float, double)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForSevenElements()
    {
        var original = (1, 2, 3, 4, 5, 6, 7);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(int, int, int, int, int, int, int)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(int, int, int, int, int, int, int)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
        Assert.Equal(original.Item3, result.Item3);
        Assert.Equal(original.Item4, result.Item4);
        Assert.Equal(original.Item5, result.Item5);
        Assert.Equal(original.Item6, result.Item6);
        Assert.Equal(original.Item7, result.Item7);
    }

    [Fact]
    public unsafe void MarshalRoundTrip_Works_ForAllPrimitiveTypes()
    {
        var original = (true, (byte)255, (short)1000, 42);
        var metadata = TypeMetadata.GetTupleTypeMetadataOrThrow<(bool, byte, short, int)>();
        var size = (int)metadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        SwiftMarshal.MarshalToSwift(original, ref span);
        var result = SwiftMarshal.MarshalFromSwift<(bool, byte, short, int)>((IntPtr)buffer);

        Assert.Equal(original.Item1, result.Item1);
        Assert.Equal(original.Item2, result.Item2);
        Assert.Equal(original.Item3, result.Item3);
        Assert.Equal(original.Item4, result.Item4);
    }

    #endregion

    #region MarshalToSwift Blittable Value Type Tests

    [Fact]
    public unsafe void MarshalToSwift_BlittableValueType_WritesCorrectly()
    {
        // Frozen blittable structs (like CGPoint, CGRect) should be writable via
        // the Unsafe.Write path in MarshalToSwift.
        var original = new TestSize { Width = 640.0, Height = 480.0 };
        var size = sizeof(TestSize);
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        var written = SwiftMarshal.MarshalToSwift(original, ref span);

        Assert.Equal(size, written);
        var result = System.Runtime.CompilerServices.Unsafe.Read<TestSize>(buffer);
        Assert.Equal(640.0, result.Width);
        Assert.Equal(480.0, result.Height);
    }

    private enum TestColor : int
    {
        Red = 0,
        Green = 1,
        Blue = 2,
    }

    [Fact]
    public unsafe void MarshalToSwift_CSharpEnum_WritesRawValue()
    {
        // Simple C# enums should be marshallable as their raw integer value.
        var value = TestColor.Green;
        var size = sizeof(TestColor);
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);

        var written = SwiftMarshal.MarshalToSwift(value, ref span);

        Assert.Equal(size, written);
        Assert.Equal(1, *(int*)buffer); // Green = 1
    }

    [Fact]
    public unsafe void MarshalFromSwift_CSharpEnum_ReadsRawValue()
    {
        // Simple C# enums should be readable via the blittable value type path.
        var buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(sizeof(int));
        try
        {
            *(int*)buffer = 2; // Blue = 2
            var result = SwiftMarshal.MarshalFromSwift<TestColor>((IntPtr)buffer);
            Assert.Equal(TestColor.Blue, result);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(buffer);
        }
    }

    /// <summary>
    /// A payload-less Swift enum with a handful of cases is stored in ONE byte, while the C# enum
    /// projecting it has a four-byte underlying type. Registered against Swift's UInt8 metadata in
    /// the test below to stand in for exactly that width mismatch.
    /// </summary>
    private enum NarrowSwiftEnum
    {
        First = 0,
        Second = 1,
        Third = 2,
    }

    [Fact]
    public unsafe void MarshalToSwift_TupleWithNarrowEnumElement_WritesOnlyTheSwiftWidth()
    {
        // The enum arm of the tuple element writer has one route into it: MarshalToSwift dispatches
        // a ValueTuple to the tuple writer, which is the arm's sole caller. It reads the element's
        // own metadata for the width, so the enum has to be registered the way a generated module
        // initializer registers a payload-less enum — an unregistered one throws before reaching it.
        TypeMetadata.RegisterMetadata(typeof(NarrowSwiftEnum), TypeMetadata.GetTypeMetadataOrThrow<byte>());

        var tupleMetadata = TypeMetadata.GetTypeMetadataOrThrow<(NarrowSwiftEnum, int)>();
        var size = (int)tupleMetadata.Size;
        var buffer = stackalloc byte[size];
        var span = new Span<byte>(buffer, size);
        span.Fill(0xAA);

        var written = SwiftMarshal.MarshalToSwift((NarrowSwiftEnum.Third, 0x11223344), ref span);

        Assert.Equal(size, written);
        Assert.Equal((byte)2, buffer[0]);
        // The bytes behind a one-byte discriminator are Swift's padding, not the enum's: writing the
        // C# underlying width instead of the Swift one would have cleared them, and on a tuple whose
        // next element started inside that span it would have cleared the element itself.
        Assert.Equal((byte)0xAA, buffer[1]);
        Assert.Equal((byte)0xAA, buffer[2]);
        Assert.Equal((byte)0xAA, buffer[3]);
        Assert.Equal(0x11223344, *(int*)(buffer + 4));

        var roundTripped = SwiftMarshal.MarshalFromSwift<(NarrowSwiftEnum, int)>((IntPtr)buffer);
        Assert.Equal(NarrowSwiftEnum.Third, roundTripped.Item1);
        Assert.Equal(0x11223344, roundTripped.Item2);
    }

    [Fact]
    public unsafe void MarshalToSwift_BlittableValueType_RoundTrip()
    {
        // Round-trip: MarshalToSwift then MarshalFromSwift for a blittable struct.
        var original = new TestSize { Width = 1920.0, Height = 1080.0 };
        var size = sizeof(TestSize);
        var buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size);
        try
        {
            var span = new Span<byte>(buffer, size);
            SwiftMarshal.MarshalToSwift(original, ref span);
            var result = SwiftMarshal.MarshalFromSwift<TestSize>((IntPtr)buffer);

            Assert.Equal(1920.0, result.Width);
            Assert.Equal(1080.0, result.Height);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(buffer);
        }
    }

    #endregion

    #region MarshalFromSwift Value Type Fallback Tests

    /// <summary>
    /// A plain blittable struct (SequentialLayout) that mimics CoreFoundation.CGSize.
    /// No ISwiftObject implementation — verifies the Unsafe.Read fallback path.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TestSize
    {
        public double Width;
        public double Height;
    }

    [Fact]
    public unsafe void MarshalFromSwift_BlittableValueType_ReadsCorrectly()
    {
        // MarshalFromSwift must handle plain value types (like CGSize) that
        // are NOT ISwiftObject, NOT primitive, NOT tuple, NOT existential container.
        // The Unsafe.Read<T>() fallback was added to handle frozen blittable structs.
        var original = new TestSize { Width = 320.0, Height = 480.0 };
        var size = sizeof(TestSize);
        var buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size);
        try
        {
            System.Runtime.CompilerServices.Unsafe.Write(buffer, original);
            var result = SwiftMarshal.MarshalFromSwift<TestSize>((IntPtr)buffer);

            Assert.Equal(320.0, result.Width);
            Assert.Equal(480.0, result.Height);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(buffer);
        }
    }

    /// <summary>
    /// A plain blittable struct with a single field — verifies the Unsafe.Read fallback
    /// handles minimal structs correctly (not captured by IsPrimitive check).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TestRadius
    {
        public double Value;
    }

    [Fact]
    public unsafe void MarshalFromSwift_BlittableValueType_SingleField_ReadsCorrectly()
    {
        // Edge case: single-field struct still routes through the value type fallback,
        // not the primitive path (since the struct itself is NOT IsPrimitive).
        var original = new TestRadius { Value = 12.5 };
        var size = sizeof(TestRadius);
        var buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size);
        try
        {
            System.Runtime.CompilerServices.Unsafe.Write(buffer, original);
            var result = SwiftMarshal.MarshalFromSwift<TestRadius>((IntPtr)buffer);

            Assert.Equal(12.5, result.Value);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(buffer);
        }
    }

    #endregion
}
