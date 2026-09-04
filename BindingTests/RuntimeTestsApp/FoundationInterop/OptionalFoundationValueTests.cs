// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// Optional Foundation values read back through a property getter.
///
/// <para>`Date?` and `Data?` are the shapes whose C# projection is a value type — a Date crosses
/// as a Double, Data as a struct — so the accessor cannot let a null reference carry Swift's
/// `nil`; it has to branch on the Optional carrier's own discriminator. Getting that wrong is
/// silent in the worst way: the consumer receives an empty-but-present value (the Swift epoch, a
/// zero-length array) that is indistinguishable from a real one. These tests therefore assert
/// both directions of the distinction — `nil` must arrive as null, and the values that LOOK like
/// the zero (the epoch itself, an empty `Data`) must arrive as present.</para>
/// </summary>
public class OptionalFoundationValueTests : TestBase
{
    public OptionalFoundationValueTests(TestResults results) : base(results) { }

    public void TestOptionalDateAndDataReadNullWhenSwiftHoldsNil()
    {
        var box = new OptionalFoundationValueBox();

        AssertFalse(box.HasWhen, "the fixture starts with an empty Date slot");
        AssertFalse(box.HasBlob, "the fixture starts with an empty Data slot");

        AssertFalse(box.When.HasValue, "a nil Date? getter reads back as null, not as the Swift epoch");
        AssertNull(box.Blob, "a nil Data? getter reads back as null, not as an empty byte[]");
    }

    public void TestOptionalDateAndDataRoundTripAValue()
    {
        var box = new OptionalFoundationValueBox();
        box.Fill(secondsSince1970: 1234.5, byteCount: 4);

        AssertTrue(box.HasWhen, "Swift holds a Date");
        AssertTrue(box.When.HasValue, "a present Date? getter reads back as a value");
        AssertApproxEqual(1234.5, box.WhenSeconds, 0.001, "Swift stored the instant the test asked for");
        AssertApproxEqual(box.WhenSeconds, box.When!.Value.ToUnixTimeMilliseconds() / 1000.0, 0.001,
            "the Date? getter round-trips the instant Swift itself reports holding");

        var blob = box.Blob;
        AssertNotNull(blob, "a present Data? getter reads back as a byte[]");
        AssertEqual(box.BlobCount, blob!.Length, "the Data? getter round-trips the length Swift itself reports holding");
        AssertEqual(4, blob.Length, "the Data? getter round-trips the length the test asked for");
        AssertEqual((byte)0, blob[0], "the Data? getter round-trips the bytes Swift stored");
        AssertEqual((byte)3, blob[3], "the Data? getter round-trips the bytes Swift stored");
    }

    /// <summary>
    /// The discrimination that a `default(T)` collapse would lose: the epoch and an empty buffer
    /// are the exact bit patterns a mishandled `nil` produces, so a getter that reported them as
    /// present would look correct here and wrong on the nil test — and one that reported them as
    /// null would look correct there and wrong here. Only asserting both pins the behaviour.
    /// </summary>
    public void TestOptionalDateAndDataDistinguishNilFromTheZeroValue()
    {
        var box = new OptionalFoundationValueBox();
        box.Fill(secondsSince1970: 0, byteCount: 0);

        AssertTrue(box.When.HasValue, "a Date at the Swift epoch is a value, not a nil");
        AssertApproxEqual(0, box.When!.Value.ToUnixTimeMilliseconds() / 1000.0, 0.001,
            "the epoch Date reads back as the epoch");

        var empty = box.Blob;
        AssertNotNull(empty, "an empty Data is a value, not a nil");
        AssertEqual(0, empty!.Length, "the empty Data reads back with no bytes");
    }

    public void TestOptionalDateAndDataGoBackToNullWhenSwiftClears()
    {
        var box = new OptionalFoundationValueBox();
        box.Fill(secondsSince1970: 99.0, byteCount: 2);
        AssertTrue(box.When.HasValue, "the slots hold values before the clear");
        AssertNotNull(box.Blob, "the slots hold values before the clear");

        box.Clear();

        AssertFalse(box.HasWhen, "Swift cleared its Date slot");
        AssertFalse(box.HasBlob, "Swift cleared its Data slot");
        AssertFalse(box.When.HasValue, "the Date? getter follows Swift back to null");
        AssertNull(box.Blob, "the Data? getter follows Swift back to null");
    }
}
