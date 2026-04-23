// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// CSM (Concrete Specialization Mechanism) coverage for DataProtocol-constrained generics.
/// Each Swift method with a D: DataProtocol parameter gets two concrete C# overloads —
/// one for Foundation.Data (InlineSwiftStruct — pinned via &amp;arg) and one for byte[]
/// (RawBuffer — pinned via fixed(byte*) with a zero-copy Data reconstruction on the
/// Swift side). DataHasher also exercises the mutating-self path.
/// </summary>
public class CsmDataProtocolTests : TestBase
{
    public CsmDataProtocolTests(TestResults results) : base(results) { }

    // --- Single-param DataProtocol (DataHasher, mutating) ---

    public void TestDataHasher_Update_ByteArray()
    {
        var hasher = new DataHasher();
        byte[] bytes = { 1, 2, 3, 4, 5 };
        hasher.Update(bytes);
        AssertEqual(5, (int)hasher.Count, "DataHasher.Update(byte[]) count");
        AssertEqual(15UL, hasher.Checksum, "DataHasher.Update(byte[]) checksum");
    }

    public void TestDataHasher_Update_Data()
    {
        var hasher = new DataHasher();
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 10, 20, 30 });
        hasher.Update(data);
        AssertEqual(3, (int)hasher.Count, "DataHasher.Update(Data) count");
        AssertEqual(60UL, hasher.Checksum, "DataHasher.Update(Data) checksum");
    }

    public void TestDataHasher_Update_Mixed()
    {
        // Mutating self accumulates across both overloads — validates the write-back
        // path applies identically to InlineSwiftStruct and RawBuffer pairings.
        var hasher = new DataHasher();
        hasher.Update(new byte[] { 1, 2, 3 });
        hasher.Update(global::Swift.Foundation.Data.FromByteArray(new byte[] { 4, 5, 6 }));
        AssertEqual(6, (int)hasher.Count, "DataHasher accumulated count");
        AssertEqual(21UL, hasher.Checksum, "DataHasher accumulated checksum");
    }

    public void TestDataHasher_Update_EmptyByteArray()
    {
        var hasher = new DataHasher();
        hasher.Update(System.Array.Empty<byte>());
        AssertEqual(0, (int)hasher.Count, "DataHasher.Update(empty byte[]) count");
        AssertEqual(0UL, hasher.Checksum, "DataHasher.Update(empty byte[]) checksum");
    }

    // --- Byte-order preservation ---
    // Count + checksum are commutative — [1,2,3] and [3,2,1] produce identical
    // values, so they can't distinguish a byte-order bug (e.g. reversed iteration
    // in a pinning loop). firstByte/lastByte witness ordering: reversing the
    // input must flip which byte lands where.

    public void TestDataHasher_Update_ByteArray_PreservesOrder()
    {
        var hasher = new DataHasher();
        hasher.Update(new byte[] { 0x10, 0x20, 0x30 });
        AssertEqual(true, (bool)hasher.HasSeenBytes, "DataHasher should record that bytes arrived");
        AssertEqual(0x10, (int)hasher.FirstByte, "DataHasher.firstByte should be the leading byte (byte[] input)");
        AssertEqual(0x30, (int)hasher.LastByte, "DataHasher.lastByte should be the trailing byte (byte[] input)");
    }

    public void TestDataHasher_Update_Data_PreservesOrder()
    {
        var hasher = new DataHasher();
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        hasher.Update(data);
        AssertEqual(true, (bool)hasher.HasSeenBytes, "DataHasher should record that bytes arrived");
        AssertEqual(0xAA, (int)hasher.FirstByte, "DataHasher.firstByte should be the leading byte (Data input)");
        AssertEqual(0xDD, (int)hasher.LastByte, "DataHasher.lastByte should be the trailing byte (Data input)");
    }

    // --- ContiguousBytes end-to-end ---
    // Parallel constraint protocol with a distinct conformer set from DataProtocol.
    // Swapping the protocol must yield an equally-valid set of specialized overloads —
    // validates the CSM pipeline is protocol-agnostic, not hand-wired to DataProtocol.

    public void TestContiguousBytesConsumer_Consume_Data()
    {
        var consumer = new ContiguousBytesConsumer();
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0xF0, 0x0F });
        consumer.Consume(data);
        AssertEqual(2, (int)consumer.BytesConsumed, "ContiguousBytesConsumer.Consume(Data) byte count");
        AssertEqual(true, (bool)consumer.HasSeenBytes, "ContiguousBytesConsumer should record that bytes arrived");
        AssertEqual(0xF0, (int)consumer.FirstByte, "ContiguousBytesConsumer.firstByte should be the leading byte");
    }

    public void TestContiguousBytesConsumer_Consume_ByteArray()
    {
        var consumer = new ContiguousBytesConsumer();
        consumer.Consume(new byte[] { 0x12, 0x34, 0x56 });
        AssertEqual(3, (int)consumer.BytesConsumed, "ContiguousBytesConsumer.Consume(byte[]) byte count");
        AssertEqual(true, (bool)consumer.HasSeenBytes, "ContiguousBytesConsumer should record that bytes arrived");
        AssertEqual(0x12, (int)consumer.FirstByte, "ContiguousBytesConsumer.firstByte should be the leading byte");
    }

    // --- Multi-param DataProtocol (MultiPATCombiner) ---
    // 2x2 cartesian product: (Data, Data) (Data, byte[]) (byte[], Data) (byte[], byte[]).

    public void TestMultiPATCombiner_DataData()
    {
        var combiner = new MultiPATCombiner();
        var a = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        var b = global::Swift.Foundation.Data.FromByteArray(new byte[] { 4, 5 });
        AssertEqual(5, (int)combiner.CombinedCount(a, b), "CombinedCount(Data, Data)");
    }

    public void TestMultiPATCombiner_DataByteArray()
    {
        var combiner = new MultiPATCombiner();
        var a = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        byte[] b = { 4, 5, 6, 7 };
        AssertEqual(7, (int)combiner.CombinedCount(a, b), "CombinedCount(Data, byte[])");
    }

    public void TestMultiPATCombiner_ByteArrayData()
    {
        var combiner = new MultiPATCombiner();
        byte[] a = { 1, 2 };
        var b = global::Swift.Foundation.Data.FromByteArray(new byte[] { 3, 4, 5 });
        AssertEqual(5, (int)combiner.CombinedCount(a, b), "CombinedCount(byte[], Data)");
    }

    public void TestMultiPATCombiner_ByteArrayByteArray()
    {
        var combiner = new MultiPATCombiner();
        byte[] a = { 1 };
        byte[] b = { 2, 3 };
        AssertEqual(3, (int)combiner.CombinedCount(a, b), "CombinedCount(byte[], byte[])");
    }

    // --- Namespace Enum CSM (Session 4) ---
    // BytesNamespace is a caseless Swift enum projected as `public static partial class`.
    // Before Session 4's EnumHandler CSM hook, static methods with method-level
    // DataProtocol generics on namespace enums never received concrete overloads —
    // only a tombstoned open-generic signature survived. These tests verify both the
    // byte[] (RawBuffer) and Foundation.Data (InlineSwiftStruct) conformer pairings
    // now emit, compile, and round-trip a value correctly through the @_cdecl wrapper.

    public void TestBytesNamespace_CountBytes_ByteArray()
    {
        byte[] bytes = { 1, 2, 3, 4, 5 };
        AssertEqual(5, (int)BytesNamespace.CountBytes(bytes),
            "BytesNamespace.CountBytes(byte[]) should return the array length");
    }

    public void TestBytesNamespace_CountBytes_Data()
    {
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 10, 20, 30 });
        AssertEqual(3, (int)BytesNamespace.CountBytes(data),
            "BytesNamespace.CountBytes(Data) should return the byte count");
    }

    public void TestBytesNamespace_CountBytes_EmptyByteArray()
    {
        AssertEqual(0, (int)BytesNamespace.CountBytes(System.Array.Empty<byte>()),
            "BytesNamespace.CountBytes(empty byte[]) should return 0");
    }

    public void TestBytesNamespace_FirstByteOrZero_ByteArray()
    {
        byte[] bytes = { 0xAB, 0xCD };
        AssertEqual(0xAB, (int)BytesNamespace.FirstByteOrZero(bytes),
            "BytesNamespace.FirstByteOrZero(byte[]) should return the leading byte");
    }

    public void TestBytesNamespace_FirstByteOrZero_Data()
    {
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0x7F, 0x01 });
        AssertEqual(0x7F, (int)BytesNamespace.FirstByteOrZero(data),
            "BytesNamespace.FirstByteOrZero(Data) should return the leading byte");
    }

    public void TestBytesNamespace_FirstByteOrZero_EmptyByteArray()
    {
        AssertEqual(0, (int)BytesNamespace.FirstByteOrZero(System.Array.Empty<byte>()),
            "BytesNamespace.FirstByteOrZero(empty byte[]) should return the sentinel 0");
    }

    public void TestBytesNamespace_FirstByteOrZero_EmptyData()
    {
        // Parallel sentinel-path coverage for the InlineSwiftStruct conformer. The byte[]
        // path exercises the fixed(byte*) + Data(bytesNoCopy: …) reconstruction; this one
        // exercises &data + pointee load. Keeping both empty-input assertions ensures a
        // future regression in either conformer's sentinel path is caught directly.
        var empty = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        AssertEqual(0, (int)BytesNamespace.FirstByteOrZero(empty),
            "BytesNamespace.FirstByteOrZero(empty Data) should return the sentinel 0");
    }

    // --- Non-throwing direct SimpleEnum / ClassPointer namespace-enum CSM ---
    // Pre-unification, the `throws &&` gate on the directReturnMapping logic meant a
    // non-throwing CSM method returning BytesKind (SimpleEnum) or BytesReport (Class)
    // emitted the raw Swift return type (`-> BytesKind` / `-> BytesReport`). @_cdecl
    // rejects both ("result type cannot be represented in Objective-C") and swiftc
    // silently strips the wrapper, so the P/Invoke call fails with "entry point not
    // found" at runtime. These tests lock in the lifted gate — if they fail with
    // EntryPointNotFoundException, the Swift wrapper for the non-throws direct-enum
    // or direct-class path regressed.

    public void TestBytesNamespace_ClassifyBytesNoThrow_Empty()
    {
        AssertEqual((int)BytesKind.Empty, (int)BytesNamespace.ClassifyBytesNoThrow(System.Array.Empty<byte>()),
            "ClassifyBytesNoThrow(empty) should return BytesKind.Empty — SimpleEnum direct-return, no throws");
    }

    public void TestBytesNamespace_ClassifyBytesNoThrow_Small_Data()
    {
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        AssertEqual((int)BytesKind.Small, (int)BytesNamespace.ClassifyBytesNoThrow(data),
            "ClassifyBytesNoThrow(3 bytes via Data) should return BytesKind.Small");
    }

    public void TestBytesNamespace_ClassifyBytesNoThrow_Large()
    {
        byte[] bytes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        AssertEqual((int)BytesKind.Large, (int)BytesNamespace.ClassifyBytesNoThrow(bytes),
            "ClassifyBytesNoThrow(10 bytes) should return BytesKind.Large");
    }

    public void TestBytesNamespace_DescribeBytesNoThrow_ByteArray()
    {
        byte[] bytes = { 0x7F, 0x2A, 0x03 };
        using var report = BytesNamespace.DescribeBytesNoThrow(bytes);
        AssertEqual(3, (int)report.ByteCount, "DescribeBytesNoThrow(byte[]).ByteCount");
        AssertEqual(0x7F, (int)report.FirstByte, "DescribeBytesNoThrow(byte[]).FirstByte");
    }

    public void TestBytesNamespace_DescribeBytesNoThrow_Empty()
    {
        using var report = BytesNamespace.DescribeBytesNoThrow(System.Array.Empty<byte>());
        AssertEqual(0, (int)report.ByteCount, "DescribeBytesNoThrow(empty).ByteCount");
        AssertEqual(0, (int)report.FirstByte, "DescribeBytesNoThrow(empty).FirstByte");
    }

    // --- Sync-throws namespace-enum CSM ---
    // Mirrors CryptoKit AEAD (AES.GCM / ChaChaPoly) Seal/Open shape: caseless namespace
    // enum + static method with a DataProtocol generic + `throws`. The emitter must
    // produce a Swift do/catch @_cdecl wrapper with an `errorOut` parameter and a
    // matching C# P/Invoke with `out IntPtr errorPtr` + SwiftMarshal.ThrowSwiftError.
    // Each test stresses one of the four cdecl-return shapes the CSM path handles:
    // direct Int, direct Bool, void, and indirect struct result.

    public void TestThrowingBytes_CountBytesOrThrow_Success_ByteArray()
    {
        byte[] bytes = { 1, 2, 3 };
        AssertEqual(3, (int)ThrowingBytesNamespace.CountBytesOrThrow(bytes),
            "CountBytesOrThrow(byte[]) should return the byte count");
    }

    public void TestThrowingBytes_CountBytesOrThrow_Success_Data()
    {
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3, 4 });
        AssertEqual(4, (int)ThrowingBytesNamespace.CountBytesOrThrow(data),
            "CountBytesOrThrow(Data) should return the byte count");
    }

    public void TestThrowingBytes_CountBytesOrThrow_Throws_EmptyByteArray()
    {
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.CountBytesOrThrow(System.Array.Empty<byte>()),
            "CountBytesOrThrow(empty byte[]) should surface the Swift error as SwiftException");
    }

    public void TestThrowingBytes_CountBytesOrThrow_Throws_EmptyData()
    {
        var empty = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.CountBytesOrThrow(empty),
            "CountBytesOrThrow(empty Data) should surface the Swift error as SwiftException");
    }

    public void TestThrowingBytes_FitsWithin_Success()
    {
        byte[] bytes = { 1, 2, 3 };
        var fits = ThrowingBytesNamespace.FitsWithin(bytes, 10);
        if (!fits) throw new AssertionException("FitsWithin(3 bytes, limit 10) should be true");
    }

    public void TestThrowingBytes_FitsWithin_FalseNoThrow()
    {
        byte[] bytes = { 1, 2, 3, 4, 5 };
        var fits = ThrowingBytesNamespace.FitsWithin(bytes, 2);
        if (fits) throw new AssertionException("FitsWithin(5 bytes, limit 2) should be false");
    }

    public void TestThrowingBytes_AssertNonEmpty_Success()
    {
        byte[] bytes = { 42 };
        ThrowingBytesNamespace.AssertNonEmpty(bytes); // no throw expected
    }

    public void TestThrowingBytes_AssertNonEmpty_Throws()
    {
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.AssertNonEmpty(System.Array.Empty<byte>()),
            "AssertNonEmpty(empty) should throw — void-return + throws path");
    }

    public void TestThrowingBytes_MakeBytesSummary_Success()
    {
        // Indirect-result return path: non-frozen BytesSummary struct lands through
        // resultPtr.initializeMemory on the Swift side, MarshalFromSwift on the C# side.
        byte[] bytes = { 0x01, 0x02, 0x04 };
        using var summary = ThrowingBytesNamespace.MakeBytesSummary(bytes);
        AssertEqual(3, (int)summary.Count, "MakeBytesSummary(byte[]).Count");
        AssertEqual(0x07, (int)summary.Xor, "MakeBytesSummary(byte[]).Xor (1^2^4 = 7)");
    }

    public void TestThrowingBytes_MakeBytesSummary_Throws()
    {
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.MakeBytesSummary(System.Array.Empty<byte>()),
            "MakeBytesSummary(empty) should throw — indirect-result + throws path");
    }

    // --- Sync-throws CSM: remaining direct-return shapes (SimpleEnum, ClassPointer) ---
    // Without these, the generated public method tries to return a raw underlying scalar
    // or IntPtr through the projected MyEnum / MyClass return type and fails to compile.

    public void TestThrowingBytes_ClassifyBytes_Empty()
    {
        AssertEqual((int)BytesKind.Empty, (int)ThrowingBytesNamespace.ClassifyBytes(System.Array.Empty<byte>()),
            "ClassifyBytes(empty) should return BytesKind.Empty — SimpleEnum direct-return on throws path");
    }

    public void TestThrowingBytes_ClassifyBytes_Small()
    {
        byte[] bytes = { 1, 2, 3 };
        AssertEqual((int)BytesKind.Small, (int)ThrowingBytesNamespace.ClassifyBytes(bytes),
            "ClassifyBytes(3 bytes) should return BytesKind.Small");
    }

    public void TestThrowingBytes_ClassifyBytes_Large_Data()
    {
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        AssertEqual((int)BytesKind.Large, (int)ThrowingBytesNamespace.ClassifyBytes(data),
            "ClassifyBytes(9 bytes via Data) should return BytesKind.Large");
    }

    public void TestThrowingBytes_ClassifyBytes_Throws()
    {
        // 0x1001 bytes trips the too-large branch; error should surface as SwiftException.
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.ClassifyBytes(new byte[0x1001]),
            "ClassifyBytes(overflow) should throw — SimpleEnum direct-return + throws error path");
    }

    public void TestThrowingBytes_DescribeBytes_Success()
    {
        byte[] bytes = { 0x2A, 0x55, 0x77 };
        using var report = ThrowingBytesNamespace.DescribeBytes(bytes);
        AssertEqual(3, (int)report.ByteCount, "DescribeBytes(byte[]).ByteCount");
        AssertEqual(0x2A, (int)report.FirstByte, "DescribeBytes(byte[]).FirstByte");
    }

    public void TestThrowingBytes_DescribeBytes_Throws()
    {
        // The error path must free the would-be SwiftHandle buffer before throwing —
        // prior to the ownership-transfer leak fix, this test would leak resultPtr.
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.DescribeBytes(System.Array.Empty<byte>()),
            "DescribeBytes(empty) should throw — ClassPointer direct-return + throws error path");
    }

    // --- Sync-throws CSM: mutating-self + Bool return (selfWriteBack ∪ directReturnMapping) ---
    // Without the Swift-side Bool→Int8 conversion after the write-back, the @_cdecl
    // declares Int8 but the body returns Bool — Swift silently strips the symbol,
    // and the P/Invoke call blows up at runtime with "entry point not found".

    public void TestThrowingByteCollector_AcceptIfSmall_True()
    {
        var collector = new ThrowingByteCollector();
        byte[] bytes = { 1, 2, 3 };
        var accepted = collector.AcceptIfSmall(bytes, 10);
        if (!accepted) throw new AssertionException("AcceptIfSmall(3 bytes, cap 10) should return true");
        AssertEqual(3, (int)collector.BytesSeen, "AcceptIfSmall should mutate _bytesSeen");
        if (!collector.Accepted) throw new AssertionException("AcceptIfSmall should mutate _accepted");
    }

    public void TestThrowingByteCollector_AcceptIfSmall_False()
    {
        var collector = new ThrowingByteCollector();
        var empty = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        var accepted = collector.AcceptIfSmall(empty, 10);
        if (accepted) throw new AssertionException("AcceptIfSmall(empty) should return false");
        AssertEqual(0, (int)collector.BytesSeen, "AcceptIfSmall(empty) bytesSeen");
    }

    public void TestThrowingByteCollector_AcceptIfSmall_Throws()
    {
        // The entry-point call must reach Swift and surface the thrown error. Prior to
        // the Swift-side Bool→Int8 conversion fix, the @_cdecl header (Int8) would not
        // match the raw `return _result` (Bool) body and swiftc would strip the symbol,
        // failing the P/Invoke with "entry point not found" — not SwiftException.
        // Swift rolls back mutations on throw (exclusive-access semantics), so we don't
        // assert on BytesSeen here; landing SwiftException proves the wrapper exists.
        var collector = new ThrowingByteCollector();
        byte[] bytes = { 1, 2, 3, 4, 5 };
        AssertThrows<SwiftException>(
            () => collector.AcceptIfSmall(bytes, 2),
            "AcceptIfSmall(5 bytes, cap 2) should throw — mutating + throws + Bool direct-return path");
    }
}
