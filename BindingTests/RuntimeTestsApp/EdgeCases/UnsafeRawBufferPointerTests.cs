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
}
