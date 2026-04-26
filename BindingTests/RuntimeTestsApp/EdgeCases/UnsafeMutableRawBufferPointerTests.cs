// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Round-trip coverage for UnsafeMutableRawBufferPointer parameter
/// marshalling — the writable companion to UnsafeRawBufferPointerTests. The
/// generator splits the Swift parameter into (ptr, len) at the @_cdecl C ABI
/// boundary and exposes <c>Span&lt;byte&gt;</c> on the C# side. These tests
/// pin spans of varying size, including the empty-span null-pointer edge
/// case, and assert that Swift-side mutations are visible to the C# caller
/// after the synchronous call returns (no copy in either direction).
/// </summary>
public class UnsafeMutableRawBufferPointerTests : TestBase
{
    public UnsafeMutableRawBufferPointerTests(TestResults results) : base(results) { }

    public void TestMultiplierStillWorks()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 7, delta: 1);
        AssertEqual(42, holder.Multiplier(6),
            "Unrelated method survives alongside the writable raw-buffer members.");
    }

    public void TestWriteLengthReturnsLength()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 0);
        Span<byte> payload = stackalloc byte[5] { 1, 2, 3, 4, 5 };
        var len = holder.WriteLength(payload);
        AssertEqual(5, len,
            "WriteLength must return the pinned span's byte count. A wrong value " +
            "indicates the (ptr, len) split mis-routed the length argument.");
    }

    public void TestWriteLengthEmptySpanUsesNullPointer()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 0);
        var len = holder.WriteLength(Span<byte>.Empty);
        AssertEqual(0, len,
            "Empty Span pins to a null-but-safe pointer; the Swift side accepts " +
            "UnsafeMutableRawPointer? and must report count=0 rather than crashing.");
    }

    public void TestFillBufferWritesBack()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0xAB, delta: 0);
        byte[] payload = new byte[5];
        var written = holder.FillBuffer(payload);
        AssertEqual(5, written,
            "FillBuffer must report the count it wrote; a mismatch indicates the " +
            "length half of the (ptr, len) split is wrong.");
        for (int i = 0; i < payload.Length; i++)
        {
            AssertEqual((byte)0xAB, payload[i],
                $"Byte at index {i} should equal fillByte (0xAB) — write-back through " +
                "the pinned span must be visible to the C# caller after return.");
        }
    }

    public void TestFillBufferStackSpan()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0x5A, delta: 0);
        Span<byte> stackSpan = stackalloc byte[3] { 0, 0, 0 };
        var written = holder.FillBuffer(stackSpan);
        AssertEqual(3, written,
            "FillBuffer across a stackalloc span pins a stack address; a wrong " +
            "count would mean the fixed block is anchoring the wrong region.");
        for (int i = 0; i < stackSpan.Length; i++)
        {
            AssertEqual((byte)0x5A, stackSpan[i],
                $"Stack-allocated byte at index {i} should equal fillByte (0x5A) — " +
                "proves stackalloc spans round-trip writes the same as managed arrays.");
        }
    }

    public void TestFillBufferEmptySpanIsNoop()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0xFF, delta: 0);
        var written = holder.FillBuffer(Span<byte>.Empty);
        AssertEqual(0, written,
            "FillBuffer on an empty span pins a null-but-safe pointer; the Swift " +
            "loop body executes zero times. Anything other than 0 indicates the " +
            "(ptr, len) split is leaking a stale length from a prior call.");
    }

    public void TestFillBufferSlicedSpan()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0xCC, delta: 0);
        byte[] backing = { 0x99, 0x99, 0x00, 0x00, 0x00, 0x99, 0x99 };
        var slice = backing.AsSpan(2, 3);
        var written = holder.FillBuffer(slice);
        AssertEqual(3, written,
            "FillBuffer on a 3-byte slice should report 3 bytes written, not the " +
            "backing array's full length — the pin must target span.Length.");
        AssertEqual((byte)0x99, backing[0],
            "Sentinel before the slice must remain 0x99 — Swift must not write past " +
            "the slice's start pointer.");
        AssertEqual((byte)0x99, backing[1],
            "Sentinel before the slice must remain 0x99 — Swift must not write past " +
            "the slice's start pointer.");
        AssertEqual((byte)0xCC, backing[2],
            "Slice byte 0 should be filled with 0xCC.");
        AssertEqual((byte)0xCC, backing[3],
            "Slice byte 1 should be filled with 0xCC.");
        AssertEqual((byte)0xCC, backing[4],
            "Slice byte 2 should be filled with 0xCC.");
        AssertEqual((byte)0x99, backing[5],
            "Sentinel after the slice must remain 0x99 — Swift must stop at the " +
            "slice's end (start + length).");
        AssertEqual((byte)0x99, backing[6],
            "Sentinel after the slice must remain 0x99 — Swift must stop at the " +
            "slice's end (start + length).");
    }

    public void TestIncrementAndSumRoundTrip()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 5);
        byte[] payload = { 1, 2, 3, 4, 5 };
        var sum = holder.IncrementAndSum(payload);
        AssertEqual(40, sum,
            "Sum after incrementing {1,2,3,4,5} by 5 should be 6+7+8+9+10 = 40 — " +
            "proves Swift saw the C#-written bytes AND that its writes round-trip " +
            "back to the C# array.");
        AssertEqual((byte)6, payload[0],
            "payload[0] should be 1+5=6 after the round-trip; a stale value here " +
            "indicates Swift wrote to a copy rather than the pinned address.");
        AssertEqual((byte)7, payload[1], "payload[1] should be 2+5=7 after the round-trip.");
        AssertEqual((byte)8, payload[2], "payload[2] should be 3+5=8 after the round-trip.");
        AssertEqual((byte)9, payload[3], "payload[3] should be 4+5=9 after the round-trip.");
        AssertEqual((byte)10, payload[4], "payload[4] should be 5+5=10 after the round-trip.");
    }

    public void TestIncrementAndSumEmptySpanIsNoop()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 5);
        var sum = holder.IncrementAndSum(Span<byte>.Empty);
        AssertEqual(0, sum,
            "Empty-span round-trip should sum to 0 — both the read loop and the " +
            "write loop must no-op at length 0. Anything else indicates the (ptr, " +
            "len) split is leaking a stale length and Swift is touching memory " +
            "past the pin.");
    }

    public void TestWriteAliasedSentinelsOverlapping()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 0);
        byte[] backing = new byte[6];
        var a = backing.AsSpan(0, 4);
        var b = backing.AsSpan(2, 4);
        var touched = holder.WriteAliasedSentinels(a, b);
        AssertEqual(8, touched,
            "Touched count should be a.Length + b.Length = 4 + 4 = 8 — proves both " +
            "(ptr, len) pairs reached Swift with the slice lengths intact, not the " +
            "backing array's length.");
        AssertEqual((byte)0x11, backing[0],
            "backing[0] is in `a` only — must remain 0x11 (the `a` write).");
        AssertEqual((byte)0x11, backing[1],
            "backing[1] is in `a` only — must remain 0x11 (the `a` write).");
        AssertEqual((byte)0x22, backing[2],
            "backing[2] is in both `a` and `b` — `b` wrote second so 0x22 wins. A " +
            "stale 0x11 here would indicate the projection silently copied `b` " +
            "rather than aliasing `a`'s backing memory.");
        AssertEqual((byte)0x22, backing[3],
            "backing[3] is in both `a` and `b` — `b` wrote second so 0x22 wins.");
        AssertEqual((byte)0x22, backing[4],
            "backing[4] is in `b` only — must be 0x22 (the `b` write).");
        AssertEqual((byte)0x22, backing[5],
            "backing[5] is in `b` only — must be 0x22 (the `b` write).");
    }

    public void TestWriteAliasedSentinelsStackSpanOverlapping()
    {
        // Companion to TestWriteAliasedSentinelsOverlapping but with a stackalloc
        // backing buffer rather than a managed array. Stackalloc has no GC
        // interaction, so this exercises the structurally-stable `fixed` pin
        // path explicitly. Both pins must still reach the same backing memory.
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0, delta: 0);
        Span<byte> backing = stackalloc byte[6];
        backing.Clear();
        var a = backing.Slice(0, 4);
        var b = backing.Slice(2, 4);
        var touched = holder.WriteAliasedSentinels(a, b);
        AssertEqual(8, touched,
            "Touched count should be a.Length + b.Length = 4 + 4 = 8 for stack " +
            "slices too — both (ptr, len) pairs must carry the slice length, not " +
            "the parent stackalloc length.");
        AssertEqual((byte)0x11, backing[0],
            "Stack backing[0] should be 0x11 — `a` write only.");
        AssertEqual((byte)0x11, backing[1],
            "Stack backing[1] should be 0x11 — `a` write only.");
        AssertEqual((byte)0x22, backing[2],
            "Stack backing[2] should be 0x22 — `b`'s write wins on the overlap; " +
            "proves stackalloc-backed aliased pins reach the same address.");
        AssertEqual((byte)0x22, backing[3],
            "Stack backing[3] should be 0x22 — `b`'s write wins on the overlap.");
        AssertEqual((byte)0x22, backing[4],
            "Stack backing[4] should be 0x22 — `b` write only.");
        AssertEqual((byte)0x22, backing[5],
            "Stack backing[5] should be 0x22 — `b` write only.");
    }

    public void TestFillBufferMediumPayloadFullScan()
    {
        // Bridges the gap between the 5-byte happy path (TestFillBufferWritesBack,
        // full-scan but tiny) and the 1 MiB spot-check (TestFillBufferLargePayload).
        // A 4 KiB buffer is large enough to cross common stride/page boundaries,
        // small enough to scan every byte without dominating runtime — catches a
        // partial-write regression that a sparse spot-check could miss.
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0xDE, delta: 0);
        const int size = 4096;
        var payload = new byte[size];
        var written = holder.FillBuffer(payload);
        AssertEqual(size, written,
            $"Medium ({size} byte) payload reports the full length; a smaller value " +
            "would indicate truncation in the (ptr, len) split.");
        for (int i = 0; i < size; i++)
        {
            if (payload[i] != 0xDE)
            {
                AssertEqual((byte)0xDE, payload[i],
                    $"Byte at index {i} should be 0xDE — full-scan catches partial " +
                    "or strided writes that a sparse spot-check would miss.");
                return;
            }
        }
    }

    public void TestFillBufferLargePayload()
    {
        using var holder = new UnsafeMutableRawBufferHolder(fillByte: 0xAA, delta: 0);
        const int size = 1 * 1024 * 1024; // 1 MiB
        var payload = new byte[size];
        var written = holder.FillBuffer(payload);
        AssertEqual(size, written,
            $"Large ({size} byte) payload reports the full length; anything lower " +
            "would indicate truncation in the (ptr, len) split.");
        // Spot-check at boundaries and a few interior offsets to confirm Swift wrote
        // the full range — full O(N) scan is unnecessary and would dominate runtime.
        AssertEqual((byte)0xAA, payload[0],
            "First byte of large payload should be filled (0xAA) — boundary spot-check.");
        AssertEqual((byte)0xAA, payload[size - 1],
            "Last byte of large payload should be filled (0xAA) — proves the loop " +
            "ran to completion rather than truncating at a smaller length.");
        AssertEqual((byte)0xAA, payload[size / 2],
            "Mid-buffer byte of large payload should be filled (0xAA) — interior " +
            "spot-check guards against partial writes.");
        AssertEqual((byte)0xAA, payload[size / 4],
            "Quarter-buffer byte of large payload should be filled (0xAA).");
        AssertEqual((byte)0xAA, payload[3 * size / 4],
            "Three-quarter-buffer byte of large payload should be filled (0xAA).");
    }
}
