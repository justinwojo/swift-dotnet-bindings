// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end coverage for <c>Foundation.UUID</c> as an <c>@_cdecl</c> wrapper PARAMETER.
///
/// <c>UUID</c> is a frozen 16-byte Swift struct, but it is also ObjC-bridgeable, so an
/// <c>@_cdecl</c> parameter declared as a bare <c>UUID</c> does not receive the 16 value bytes —
/// Swift lowers it to a single <c>NSUUID</c> object pointer. The managed side passes a
/// <c>System.Guid</c> by value (16 bytes), so the wrapper would read the first 8 bytes of the
/// Guid as an object pointer and every later argument would shift by a slot. The return side
/// already reinterprets the 16 bytes verbatim, so the parameter side must be its exact inverse.
///
/// The value used throughout is built from fixed bytes rather than <c>Guid.NewGuid()</c> so a
/// mis-lowered argument cannot coincidentally produce the expected answer, and so the textual
/// conventions on both sides can be asserted rather than assumed.
/// </summary>
public class UuidParameterAbiTests : TestBase
{
    public UuidParameterAbiTests(TestResults results) : base(results) { }

    /// <summary>Byte pattern A, in Swift's <c>uuid</c>-tuple / memory order.</summary>
    private static readonly byte[] BytesA =
    {
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x00, 0x00, 0x00, 0x42
    };

    /// <summary>Byte pattern B, distinct from A in every group.</summary>
    private static readonly byte[] BytesB =
    {
        0xA0, 0xB1, 0xC2, 0xD3, 0xE4, 0xF5, 0x06, 0x17,
        0x28, 0x39, 0x4A, 0x5B, 0x6C, 0x7D, 0x8E, 0x9F
    };

    /// <summary>
    /// <c>new Guid(byte[16])</c> and <c>Guid.ToByteArray()</c> are inverses, and on a
    /// little-endian host the array order is also the struct's memory order — the same order
    /// Swift's <c>uuid</c> tuple uses. So a Guid built from these bytes carries exactly the
    /// bytes Swift will observe.
    /// </summary>
    private static Guid GuidFromBytes(byte[] bytes) => new Guid(bytes);

    private static long ByteSum(byte[] bytes)
    {
        long sum = 0;
        foreach (var b in bytes)
            sum += b;
        return sum;
    }

    /// <summary>
    /// Renders the bytes the way Swift's <c>UUID.uuidString</c> does: straight memory order,
    /// uppercase, grouped 8-4-4-4-12.
    /// </summary>
    private static string SwiftUuidString(byte[] bytes)
    {
        var hex = new System.Text.StringBuilder(36);
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10)
                hex.Append('-');
            hex.Append(bytes[i].ToString("X2"));
        }
        return hex.ToString();
    }

    public void TestReturnSide_MakeUuidFromBytes_MatchesGuidBuiltFromSameBytes()
    {
        var expected = GuidFromBytes(BytesA);
        var actual = TestLibFunctions.MakeUuidFromBytes(
            BytesA[0], BytesA[1], BytesA[2], BytesA[3], BytesA[4], BytesA[5], BytesA[6], BytesA[7],
            BytesA[8], BytesA[9], BytesA[10], BytesA[11], BytesA[12], BytesA[13], BytesA[14], BytesA[15]);
        AssertEqual(expected, actual, "UUID return side reinterprets the 16 bytes verbatim as System.Guid");
    }

    public void TestFreeFunction_UuidParameterDeliversEveryByte()
    {
        var value = GuidFromBytes(BytesA);
        AssertEqual(ByteSum(BytesA), TestLibFunctions.UuidByteSumOf(value),
            "Every byte of a UUID free-function parameter reaches Swift intact");
    }

    /// <summary>
    /// Pins the textual relation between the two sides instead of papering over it: Swift prints
    /// the 16 bytes in memory order, while <c>Guid.ToString()</c> reverses the first three groups
    /// because <c>Guid</c> stores them as little-endian integer fields. Both renderings describe
    /// the same 16 bytes; only the grouping convention differs.
    /// </summary>
    public void TestFreeFunction_SwiftTextIsMemoryOrderWhileGuidTextReversesFirstThreeGroups()
    {
        var value = GuidFromBytes(BytesA);

        AssertEqual(SwiftUuidString(BytesA), TestLibFunctions.UuidTextOf(value),
            "Swift uuidString renders the parameter's bytes in memory order");

        var reordered = new byte[]
        {
            BytesA[3], BytesA[2], BytesA[1], BytesA[0],
            BytesA[5], BytesA[4],
            BytesA[7], BytesA[6],
            BytesA[8], BytesA[9], BytesA[10], BytesA[11], BytesA[12], BytesA[13], BytesA[14], BytesA[15]
        };
        AssertEqual(SwiftUuidString(reordered), value.ToString().ToUpperInvariant(),
            "Guid.ToString() reverses the first three groups relative to Swift's uuidString");
    }

    public void TestFreeFunction_UuidPastIntegerRegisterBoundary()
    {
        var value = GuidFromBytes(BytesB);
        var leading = 1L + 2 + 3 + 4 + 5 + 6 + 7;
        AssertEqual(leading + ByteSum(BytesB),
            TestLibFunctions.UuidPastRegisterBoundary(1, 2, 3, 4, 5, 6, 7, value),
            "A UUID parameter at/after the last integer register keeps its bytes and does not shift later arguments");
    }

    public void TestFreeFunction_TwoAdjacentUuidParameters()
    {
        var first = GuidFromBytes(BytesA);
        var second = GuidFromBytes(BytesB);
        AssertEqual(ByteSum(BytesA) * 1000 + ByteSum(BytesB),
            TestLibFunctions.UuidPairByteSums(first, second),
            "Adjacent UUID parameters each consume the right number of argument slots");
    }

    public void TestFreeFunction_OptionalUuidParameterCarriesNoneAndSome()
    {
        AssertEqual(-1L, TestLibFunctions.OptionalUuidByteSum(null),
            "nil Optional<UUID> parameter arrives as nil");
        AssertEqual(ByteSum(BytesB), TestLibFunctions.OptionalUuidByteSum(GuidFromBytes(BytesB)),
            "Some(UUID) parameter arrives with every byte intact");
    }

    public void TestInitializer_UuidParameterRoundTripsThroughStoredProperty()
    {
        var value = GuidFromBytes(BytesA);
        using var registration = new DeviceRegistration(value);

        AssertEqual(value, registration.Identifier,
            "A UUID passed to an initializer reads back identically from the stored property");
        AssertEqual(ByteSum(BytesA), registration.GetStoredByteSum(),
            "Swift sees every byte of the initializer's UUID argument");
    }

    public void TestInstanceMethod_UuidParameterDeliversEveryByte()
    {
        using var registration = new DeviceRegistration(GuidFromBytes(BytesA));
        AssertEqual(ByteSum(BytesB), registration.ByteSum(GuidFromBytes(BytesB)),
            "Every byte of a UUID instance-method parameter reaches Swift intact");
    }

    public void TestStaticMethod_UuidParameterDeliversFirstByte()
    {
        AssertEqual((int)BytesB[0], DeviceRegistration.FirstByte(GuidFromBytes(BytesB)),
            "A UUID static-method parameter arrives in memory order (first byte first)");
    }

    public void TestPropertySetter_UuidParameterRoundTrips()
    {
        using var registration = new DeviceRegistration(GuidFromBytes(BytesA));
        var replacement = GuidFromBytes(BytesB);

        registration.ReplacementIdentifier = replacement;

        AssertEqual(replacement, registration.ReplacementIdentifier,
            "A UUID property setter stores the value the caller passed");
        AssertEqual(ByteSum(BytesB), registration.GetReplacementByteSum(),
            "Swift sees every byte of the UUID handed to the property setter");
        AssertEqual(GuidFromBytes(BytesA), registration.Identifier,
            "Setting the replacement leaves the initializer-stored UUID untouched");
    }
}
