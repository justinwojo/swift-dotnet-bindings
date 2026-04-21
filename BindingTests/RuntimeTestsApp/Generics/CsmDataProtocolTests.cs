// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
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
}
