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

    // --- Namespace Enum CSM ---
    // BytesNamespace is a caseless Swift enum projected as `public static partial class`.
    // Before the EnumHandler CSM hook, static methods with method-level
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

    // --- Sync-throws CSM: missing throw test for FitsWithin (primitive Bool direct-return) ---
    // FitsWithin returned only happy-path coverage above. The error branch (>0x1000 bytes
    // → BytesValidationError.tooLarge) is the same Bool→Int8 sentinel + errorOut path that
    // ClassifyBytes/MakeBytesSummary cover for other shapes; explicitly testing it locks
    // the primitive-direct-return throws sentinel return into the regression set.
    public void TestThrowingBytes_FitsWithin_Throws()
    {
        AssertThrows<SwiftException>(
            () => ThrowingBytesNamespace.FitsWithin(new byte[0x1001], 0x10000),
            "FitsWithin(>0x1000 bytes) should throw — primitive Bool direct-return + throws error path");
    }

    // --- Sync-throws CSM: localized-description round-trip across all return shapes ---
    // SBW_GetErrorDescription emits String(describing: error) for Swift Error conformers.
    // BytesValidationError implements CustomStringConvertible so String(describing:) returns
    // the `description` property — "BytesValidationError.empty" or "BytesValidationError.tooLarge(N)".
    // These tests pin the message round-trip so a regression in either the Swift-side
    // String(describing:) extraction or the C# Marshal.PtrToStringUTF8 path is caught.
    // Coverage spans all 4 working return-shape branches (Void, primitive, struct, enum)
    // so a per-shape regression in the catch path's Unmanaged.passRetained → C# error
    // marshalling can't slip through.

    private static SwiftException CaptureSwiftException(System.Action action, string label)
    {
        try
        {
            action();
        }
        catch (SwiftException e)
        {
            return e;
        }
        throw new AssertionException($"{label}: expected SwiftException but no exception was thrown");
    }

    private void AssertContains(string expected, string actual, string label)
    {
        if (actual is null || !actual.Contains(expected, System.StringComparison.Ordinal))
            throw new AssertionException($"{label}: expected message to contain '{expected}', got '{actual ?? "<null>"}'");
    }

    public void TestThrowingBytes_LocalizedDescription_Void()
    {
        // Void return: catch path emits errorOut.pointee then returns Void with no sentinel.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.AssertNonEmpty(System.Array.Empty<byte>()),
            "AssertNonEmpty(empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "AssertNonEmpty(empty) — Void return shape should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingBytes_LocalizedDescription_PrimitiveInt()
    {
        // Primitive Int direct-return: catch path returns the Int8/Int sentinel and the
        // C# side discards _result after the errorPtr check.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.CountBytesOrThrow(System.Array.Empty<byte>()),
            "CountBytesOrThrow(empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "CountBytesOrThrow(empty) — direct primitive return shape should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingBytes_LocalizedDescription_PrimitiveBool_TooLarge()
    {
        // Primitive Bool direct-return + .tooLarge case: locks in payload-bearing case
        // round-tripping through CustomStringConvertible.description.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.FitsWithin(new byte[0x1001], 0x10000),
            "FitsWithin(>0x1000)");
        AssertContains("BytesValidationError.tooLarge(4097)", e.Message,
            "FitsWithin(>0x1000) — direct Bool return should round-trip the .tooLarge(N) payload-bearing description");
    }

    public void TestThrowingBytes_LocalizedDescription_Struct()
    {
        // Indirect-result (needsResultPtr) shape: catch path emits errorOut.pointee with
        // no sentinel and the C# side frees the would-be ownership-transfer buffer before
        // ThrowSwiftError. Same generic-return code path as a hypothetical `func transform<D>(_: D) throws -> D`.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.MakeBytesSummary(System.Array.Empty<byte>()),
            "MakeBytesSummary(empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "MakeBytesSummary(empty) — indirect-result struct return shape should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingBytes_LocalizedDescription_Enum()
    {
        // SimpleEnum direct-return shape: BytesKind has a raw Int8 ABI; catch path returns
        // the rawValue sentinel and the C# side casts _result to (BytesKind) only after
        // the errorPtr check skips that branch.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.ClassifyBytes(new byte[0x1001]),
            "ClassifyBytes(>0x1000)");
        AssertContains("BytesValidationError.tooLarge(4097)", e.Message,
            "ClassifyBytes(>0x1000) — SimpleEnum direct-return should round-trip the .tooLarge(N) payload-bearing description");
    }

    public void TestThrowingBytes_LocalizedDescription_Class()
    {
        // ClassPointer direct-return shape: catch path returns the pointer sentinel and
        // the C# side discards the _result IntPtr without wrapping in a SwiftHandle.
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.DescribeBytes(System.Array.Empty<byte>()),
            "DescribeBytes(empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "DescribeBytes(empty) — ClassPointer direct-return should round-trip BytesValidationError.empty description");
    }

    // --- Sync-throws CSM: generic-parameter return shape (T) ---
    // ThrowingItemNamespace.ValidateAndReturn<T: SearchableItem>(_:_:) throws -> T
    // exercises the @_cdecl `returnsGenericParam=true → needsResultPtr=true` path.
    // SongItem/AlbumItem/ArtistItem (frozen-struct conformers from spec hints) round-trip
    // through resultPtr.initializeMemory(as: T.self, ...) on the Swift side and
    // SwiftMarshal.MarshalFromSwift<T>(resultPtr) on the C# side. Catch path emits
    // errorOut.pointee with no sentinel return (indirect-result shape returns Void).

    public void TestThrowingItem_ValidateAndReturn_SongItem_Success()
    {
        var item = new SongItem();
        var roundTripped = ThrowingItemNamespace.ValidateAndReturn(item, true);
        AssertNotNull(roundTripped,
            "ValidateAndReturn(SongItem, true) — generic-return shape should round-trip the conformer");
        AssertEqual(typeof(SongItem), roundTripped.GetType(),
            "ValidateAndReturn(SongItem, true) — returned runtime type must match the SongItem specialization");
    }

    public void TestThrowingItem_ValidateAndReturn_AlbumItem_Success()
    {
        var item = new AlbumItem();
        var roundTripped = ThrowingItemNamespace.ValidateAndReturn(item, true);
        AssertNotNull(roundTripped,
            "ValidateAndReturn(AlbumItem, true) — generic-return shape should round-trip the conformer");
        AssertEqual(typeof(AlbumItem), roundTripped.GetType(),
            "ValidateAndReturn(AlbumItem, true) — returned runtime type must match the AlbumItem specialization");
    }

    public void TestThrowingItem_ValidateAndReturn_ArtistItem_Success()
    {
        var item = new ArtistItem();
        var roundTripped = ThrowingItemNamespace.ValidateAndReturn(item, true);
        AssertNotNull(roundTripped,
            "ValidateAndReturn(ArtistItem, true) — generic-return shape should round-trip the conformer");
        AssertEqual(typeof(ArtistItem), roundTripped.GetType(),
            "ValidateAndReturn(ArtistItem, true) — returned runtime type must match the ArtistItem specialization");
    }

    public void TestThrowingItem_ValidateAndReturn_SongItem_Throws()
    {
        // Generic-return shape catch path: errorOut.pointee = Unmanaged.passRetained(...)
        // and the @_cdecl returns Void. Indirect-result shapes (resultPtr or generic-return)
        // need no sentinel return, unlike direct-return shapes which do.
        var e = CaptureSwiftException(
            () => ThrowingItemNamespace.ValidateAndReturn(new SongItem(), false),
            "ValidateAndReturn(SongItem, false)");
        AssertContains("BytesValidationError.empty", e.Message,
            "ValidateAndReturn(SongItem, false) — generic-return shape should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingItem_ValidateAndReturn_AlbumItem_Throws()
    {
        // Middle conformer of the three specializations — keeps red-path coverage
        // symmetric with the success path so a regression in any one specialization's
        // catch arm is caught.
        var e = CaptureSwiftException(
            () => ThrowingItemNamespace.ValidateAndReturn(new AlbumItem(), false),
            "ValidateAndReturn(AlbumItem, false)");
        AssertContains("BytesValidationError.empty", e.Message,
            "ValidateAndReturn(AlbumItem, false) — generic-return shape should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingItem_ValidateAndReturn_ArtistItem_Throws()
    {
        // Same generic-return throws path on a different conformer to lock in that all
        // three specialization wrappers carry the errorOut catch arm consistently.
        var e = CaptureSwiftException(
            () => ThrowingItemNamespace.ValidateAndReturn(new ArtistItem(), false),
            "ValidateAndReturn(ArtistItem, false)");
        AssertContains("BytesValidationError.empty", e.Message,
            "ValidateAndReturn(ArtistItem, false) — generic-return shape should round-trip BytesValidationError.empty description");
    }

    // --- Sync-throws CSM: localized-description round-trip through the Foundation.Data overload ---
    // CSM emits a separate @_cdecl wrapper per (constraint, conformer) pairing. The byte[]-side
    // tests above only pin the raw-buffer specialization's catch arm; a regression localized to
    // the Foundation.Data specialization (InlineSwiftStruct pinning via &arg instead of fixed
    // (byte*)) would pass them. Cover one direct-return and two indirect-shape branches through
    // the Data overload so the Data specialization's catch arm is also in the regression set.

    public void TestThrowingBytes_LocalizedDescription_Data_Void()
    {
        // Data overload of the Void return shape — same catch-arm logic as the byte[] side
        // but a different emitted specialization symbol.
        using var data = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.AssertNonEmpty(data),
            "AssertNonEmpty(Data empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "AssertNonEmpty(Data empty) — Void return shape on the Data specialization should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingBytes_LocalizedDescription_Data_PrimitiveInt()
    {
        // Data overload of the primitive-Int direct-return shape. Pins the Data
        // specialization's sentinel-return catch arm alongside the byte[] side.
        using var data = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.CountBytesOrThrow(data),
            "CountBytesOrThrow(Data empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "CountBytesOrThrow(Data empty) — primitive-Int direct-return on the Data specialization should round-trip BytesValidationError.empty description");
    }

    public void TestThrowingBytes_LocalizedDescription_Data_Struct()
    {
        // Data overload of the struct indirect-result shape (needsResultPtr). Catch
        // arm frees the would-be ownership-transfer buffer and returns Void; this
        // test proves that happens under the Data specialization too.
        using var data = global::Swift.Foundation.Data.FromByteArray(System.Array.Empty<byte>());
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.MakeBytesSummary(data),
            "MakeBytesSummary(Data empty)");
        AssertContains("BytesValidationError.empty", e.Message,
            "MakeBytesSummary(Data empty) — struct indirect-result shape on the Data specialization should round-trip BytesValidationError.empty description");
    }

    // --- Sync-throws CSM: generic-parameter return shape payload oracle ---
    // The three SongItem/AlbumItem/ArtistItem success tests above only assert the
    // managed wrapper type survives; because those conformers are empty marker
    // structs there is no payload to witness. TaggedSearchItem carries an `id: UInt32`
    // stored field, so a round-trip through ValidateAndReturnTagged(_, true) proves the
    // `resultPtr` actually carried the input value across the @_cdecl boundary
    // rather than e.g. default-initializing a fresh TaggedSearchItem on the Swift side.
    // TaggedSearchItem conforms to ValidatableItem (not SearchableItem) so the oracle
    // fixture stays isolated from the GenericContainer / ElementBoundContainer CSM
    // matrices — adding a payload-bearing conformer there would quietly broaden their
    // emitted surface without matching test coverage.

    public void TestThrowingItem_ValidateAndReturnTagged_TaggedSearchItem_Success_RoundTripsPayload()
    {
        using var item = new TaggedSearchItem(1234u);
        using var roundTripped = ThrowingItemNamespace.ValidateAndReturnTagged(item, true);
        AssertNotNull(roundTripped,
            "ValidateAndReturnTagged(TaggedSearchItem(1234), true) — generic-return shape should round-trip the conformer");
        AssertEqual(typeof(TaggedSearchItem), roundTripped.GetType(),
            "ValidateAndReturnTagged(TaggedSearchItem(1234), true) — returned runtime type must match the TaggedSearchItem specialization");
        AssertEqual(1234u, roundTripped.Id,
            "ValidateAndReturnTagged(TaggedSearchItem(1234), true) — payload Id must round-trip through resultPtr, proving the Swift side copied the input into the indirect result buffer");
    }

    public void TestThrowingItem_ValidateAndReturnTagged_TaggedSearchItem_Throws()
    {
        // Throw path on the payload-bearing conformer — exercises the generic-return
        // catch arm on a specialization whose type has a non-trivial stored property
        // (vs. the empty marker structs). Proves the errorOut.pointee assignment and
        // resultPtr buffer free happen regardless of T's layout.
        using var item = new TaggedSearchItem(1234u);
        var e = CaptureSwiftException(
            () => ThrowingItemNamespace.ValidateAndReturnTagged(item, false),
            "ValidateAndReturnTagged(TaggedSearchItem(1234), false)");
        AssertContains("BytesValidationError.empty", e.Message,
            "ValidateAndReturnTagged(TaggedSearchItem(1234), false) — generic-return throws on a payload-bearing conformer should still round-trip BytesValidationError.empty description");
    }

    // --- Sync-throws CSM with a non-frozen struct as a non-generic param ---
    //
    // Mirrors the CryptoKit AES.GCM.seal/ChaChaPoly.seal regression pattern: a
    // sync-throws CSM generic over DataProtocol that takes a non-frozen struct
    // (AeadKeyHandle, analog of SymmetricKey) by value. The wrapper must
    // reconstruct the struct via .pointee (a value-witness-table-aware load),
    // not unsafeBitCast on the pointer — the latter is only correct for Swift
    // classes. With the wrong reconstruction, key.bitCount reads the pointer's
    // low bits, and the Swift guard throws BytesValidationError.tooLarge(bits)
    // instead of returning bits + bytes.count.

    public void TestThrowingBytes_SealLengthWithKey_ByteArray_RoundTrip()
    {
        using var key = new AeadKeyHandle(256);
        byte[] bytes = { 1, 2, 3, 4 };
        var result = (int)ThrowingBytesNamespace.SealLengthWithKey(bytes, key);
        AssertEqual(260, result,
            "SealLengthWithKey(byte[], AeadKeyHandle(256)) — non-frozen struct param must round-trip via .pointee, not unsafeBitCast(OpaquePointer); a wrong reconstruction reads the pointer bits and throws BytesValidationError.tooLarge(<pointer-bits>)");
    }

    public void TestThrowingBytes_SealLengthWithKey_Data_RoundTrip()
    {
        using var key = new AeadKeyHandle(128);
        // Data path mirrors AES.GCM.Seal(Data, SymmetricKey) — same emitter pairing
        // for a DataProtocol conformer with a non-frozen struct param.
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 9, 9 });
        var result = (int)ThrowingBytesNamespace.SealLengthWithKey(data, key);
        AssertEqual(130, result,
            "SealLengthWithKey(Data, AeadKeyHandle(128)) — Data conformer + non-frozen struct param round-trip");
    }

    public void TestThrowingBytes_SealLengthWithKey_PayloadObservability()
    {
        // Distinguishes "param marshalling broken" from "this fixture happens to
        // align with the pointer's low bits". A 192 key whose bitCount round-trips
        // is a different value than 128 or 256 — proves the wrapper actually reads
        // the struct's payload buffer, not a fixed pointer pattern.
        using var key192 = new AeadKeyHandle(192);
        var result192 = (int)ThrowingBytesNamespace.SealLengthWithKey(new byte[] { 1 }, key192);
        AssertEqual(193, result192,
            "SealLengthWithKey with AeadKeyHandle(192) — payload value must be observable inside the @_cdecl body");
    }

    public void TestThrowingBytes_SealLengthWithKey_EmptyBytes_Throws()
    {
        using var key = new AeadKeyHandle(256);
        var e = CaptureSwiftException(
            () => ThrowingBytesNamespace.SealLengthWithKey(System.Array.Empty<byte>(), key),
            "SealLengthWithKey(empty byte[], AeadKeyHandle(256))");
        AssertContains("BytesValidationError.empty", e.Message,
            "SealLengthWithKey(empty byte[]) — sync-throws CSM with a non-frozen struct param must still surface the Swift error description on the catch arm");
    }
}
