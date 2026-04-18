// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Round-trip coverage for UnsafeRawBufferPointer parameter marshalling. The
/// generator splits the Swift parameter into (ptr, len) at the @_cdecl C ABI
/// boundary and exposes ReadOnlySpan&lt;byte&gt; on the C# side. These tests
/// pin spans of varying size — including the empty-span null-pointer edge
/// case — and assert the Swift side sees the exact bytes passed in.
/// </summary>
public class UnsafeRawBufferPointerTests : TestBase
{
    public UnsafeRawBufferPointerTests(TestResults results) : base(results) { }

    public void TestMultiplierStillWorks()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 7);
        AssertEqual(42, holder.Multiplier(6),
            "Unrelated method survives alongside the raw-buffer members.");
    }

    public void TestReadBufferReturnsLength()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        byte[] payload = { 1, 2, 3, 4, 5 };
        var len = holder.ReadBuffer(payload);
        AssertEqual(5, len,
            "ReadBuffer must return the pinned span's byte count. A wrong value " +
            "indicates the (ptr, len) split mis-routed the length argument.");
    }

    public void TestReadBufferEmptySpanUsesNullPointer()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        var len = holder.ReadBuffer(ReadOnlySpan<byte>.Empty);
        AssertEqual(0, len,
            "Empty ReadOnlySpan pins to a null pointer; the Swift side accepts " +
            "UnsafeRawPointer? and must report count=0 rather than crashing.");
    }

    public void TestSumBytesMatchesPayload()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        byte[] payload = { 10, 20, 30, 40 };
        var sum = holder.SumBytes(payload);
        AssertEqual(100, sum,
            "SumBytes must dereference the pinned bytes; a wrong sum indicates " +
            "the pointer half of the split ABI is not reaching the Swift side.");
    }

    public void TestSumBytesStackSpan()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        Span<byte> stackSpan = stackalloc byte[3] { 100, 150, 200 };
        var sum = holder.SumBytes(stackSpan);
        AssertEqual(450, sum,
            "SumBytes across a stackalloc span proves the pin survives without " +
            "a backing GC object — the fixed block anchors the stack address.");
    }

    public void TestSumBytesSlicedSpan()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        byte[] backing = { 99, 99, 10, 20, 30, 99, 99 };
        var slice = backing.AsSpan(2, 3);
        var len = holder.ReadBuffer(slice);
        var sum = holder.SumBytes(slice);
        AssertEqual(3, len,
            "Sliced span reports the slice length, not the backing array length — " +
            "proves the pin targets span.Length rather than array.Length.");
        AssertEqual(60, sum,
            "Sliced span sums only the slice bytes (10+20+30), ignoring the 99 " +
            "sentinels on either side of the window.");
    }

    public void TestSumBytesAliasedSlicesSameBacking()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        byte[] backing = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var head = backing.AsSpan(0, 4);
        var tail = backing.AsSpan(6, 4);
        var headSum = holder.SumBytes(head);
        var tailSum = holder.SumBytes(tail);
        AssertEqual(10, headSum,
            "Leading slice sums 1..4; aliased pins on the same backing array must " +
            "each land on their own (ptr, len) pair without bleeding into the other.");
        AssertEqual(34, tailSum,
            "Trailing slice sums 7..10; proves the second pin picks up a distinct " +
            "start offset rather than re-using the earlier slice's pointer.");
    }

    public void TestSumBytesLargePayload()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 1);
        const int size = 64 * 1024;
        var payload = new byte[size];
        Array.Fill(payload, (byte)1);
        var len = holder.ReadBuffer(payload);
        var sum = holder.SumBytes(payload);
        AssertEqual(size, len,
            $"Large ({size} byte) payload reports the full length; anything lower " +
            "would indicate truncation in the (ptr, len) split.");
        AssertEqual(size, sum,
            $"Large payload of repeated 0x01 sums to {size}; a mismatch indicates " +
            "the Swift side is not walking the entire pinned range.");
    }
}
