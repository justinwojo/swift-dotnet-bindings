// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Parameters typed <c>UnsafeBufferPointer&lt;T&gt;</c> and <c>UnsafeMutableBufferPointer&lt;T&gt;</c>
/// on static, instance, struct, free and async members. Each member computes something from the
/// elements, so the base address and count must both arrive intact; the mutable members write
/// through the buffer, so the caller observes Swift reaching its own memory.
/// </summary>
public class TypedBufferPointerParamTests : TestBase
{
    public TypedBufferPointerParamTests(TestResults results) : base(results) { }

    public unsafe void TestStaticThrowingParserReadsBytesAndByteArray()
    {
        byte[] html = [1, 2, 3, 4];
        fixed (byte* p = html)
        {
            using var document = TypedBufferReader.Parse(new UnsafeBufferPointer<byte>(p, html.Length), new byte[] { 10, 20 });
            AssertEqual(6, document.ByteCount, "both inputs were counted");
            AssertEqual(40, document.Checksum, "every element of both inputs was read");
        }

        AssertThrows<SwiftException>(
            () => TypedBufferReader.Parse(new UnsafeBufferPointer<byte>(null, 0), new byte[] { 1 }),
            "an empty buffer throws the Swift error");
    }

    public unsafe void TestInstanceMembersReadTheCallersElements()
    {
        using var reader = new TypedBufferReader();
        byte[] bytes = [200, 100, 50];
        int[] values = [7, 8, 9];
        fixed (byte* pb = bytes)
        fixed (int* pv = values)
        {
            AssertEqual((nint)350, reader.Sum(new UnsafeBufferPointer<byte>(pb, bytes.Length)), "all bytes were summed");
            AssertEqual(7, reader.FirstOrMinusOne(new UnsafeBufferPointer<int>(pv, values.Length)), "the first element arrived");
        }
        AssertEqual(-1, reader.FirstOrMinusOne(new UnsafeBufferPointer<int>(null, 0)), "a buffer without a base address arrives empty");
    }

    public unsafe void TestMutableBufferWritesReachTheCallersMemory()
    {
        using var reader = new TypedBufferReader();
        int[] values = new int[4];
        long[] wide = [3, -4, 5];
        fixed (int* pv = values)
        fixed (long* pw = wide)
        {
            reader.Fill(new UnsafeMutableBufferPointer<int>(pv, values.Length), 10);
            AssertEqual((nint)3, TypedBufferReader.DoubleInPlace(new UnsafeMutableBufferPointer<long>(pw, wide.Length)), "the count arrived");
        }

        AssertEqual("10,11,12,13", string.Join(",", values), "Swift wrote every element in place");
        AssertEqual("6,-8,10", string.Join(",", wide), "the static throwing member doubled every element in place");
        AssertThrows<SwiftException>(
            () => TypedBufferReader.DoubleInPlace(new UnsafeMutableBufferPointer<long>(null, 0)),
            "an empty mutable buffer throws the Swift error");
    }

    public unsafe void TestStructMemberAndFreeFunction()
    {
        using var stats = new TypedBufferStats();
        double[] samples = [1.5, 2.5, 5.0];
        long[] totals = [long.MaxValue - 1, 1, 5];
        fixed (double* ps = samples)
        fixed (long* pt = totals)
        {
            AssertEqual(3.0, stats.Mean(new UnsafeBufferPointer<double>(ps, samples.Length)), "the struct member read every sample");
            AssertEqual(long.MinValue + 4, TestLibFunctions.TypedBufferTotal(new UnsafeBufferPointer<long>(pt, totals.Length)),
                "the free function read every element, wrapping like Swift's &+");
        }
    }

    public async Task TestAsyncMemberReadsTheBufferBeforeSuspending()
    {
        using var reader = new TypedBufferReader();
        var values = AllocateInts(4, 5, 6);
        try
        {
            var sum = await StartSum(reader, values, 3);
            AssertEqual((nint)15, sum, "the async member read every element");
        }
        finally
        {
            FreeInts(values);
        }
    }

    private static unsafe nint AllocateInts(params int[] items)
    {
        var values = (int*)global::System.Runtime.InteropServices.NativeMemory.Alloc((nuint)items.Length, sizeof(int));
        for (int i = 0; i < items.Length; i++) values[i] = items[i];
        return (nint)values;
    }

    private static unsafe void FreeInts(nint values) =>
        global::System.Runtime.InteropServices.NativeMemory.Free((void*)values);

    private static unsafe Task<nint> StartSum(TypedBufferReader reader, nint values, int count) =>
        reader.SumAsync(new UnsafeBufferPointer<int>((int*)values, count));
}
