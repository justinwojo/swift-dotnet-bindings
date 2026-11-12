// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Initializers on generic structs constrained by Self-requirement protocols (Decodable, Encodable,
/// Equatable) plus Sendable. They bind as direct Swift-convention calls, and the callee takes one
/// witness table per conformance after the generic metadata. Each init body reads every witness
/// table, so the values below are right only when the call passes all of them in order.
/// </summary>
public class DescriptorWitnessConstructorTests : TestBase
{
    public DescriptorWitnessConstructorTests(TestResults results) : base(results) { }

    public void TestBoxInitReadsEveryWitnessTable_Payload()
    {
        using var value = new WitnessPayload(7);
        using var probe = new WitnessPayload(7);
#pragma warning disable SB0001 // A generic-typed init parameter keeps the direct-call caution.
        using var box = new WitnessReadingBox<WitnessPayload>(value, probe);
#pragma warning restore SB0001
        AssertTrue(box.MatchesProbe, "value == probe through the Equatable witness table");
        AssertEqual(10, (int)box.EncodedLength, "{\"code\":7} through the Encodable witness table");
        AssertTrue(box.RoundTrips, "decode(encode(value)) == value through the Decodable witness table");
        using var stored = box.Value;
        AssertEqual(7, stored.Code, "stored value");
    }

    public void TestBoxInitReadsEveryWitnessTable_Mismatch()
    {
        using var value = new WitnessPayload(3);
        using var probe = new WitnessPayload(4);
#pragma warning disable SB0001
        using var box = new WitnessReadingBox<WitnessPayload>(value, probe);
#pragma warning restore SB0001
        AssertFalse(box.MatchesProbe, "3 != 4 through the Equatable witness table");
        AssertTrue(box.RoundTrips, "round trip still holds");
    }

    public void TestBoxInitReadsEveryWitnessTable_StdlibScalar()
    {
#pragma warning disable SB0001
        using var box = new WitnessReadingBox<int>(12345, 12345);
#pragma warning restore SB0001
        AssertTrue(box.MatchesProbe, "Int32 == through the Equatable witness table");
        AssertEqual(5, (int)box.EncodedLength, "12345 through the Encodable witness table");
        AssertTrue(box.RoundTrips, "Int32 round trip");
        AssertEqual(12345, box.Value, "stored Int32");
    }

    public void TestQueryInitReadsEveryWitnessTable()
    {
        using var query = new WitnessReadingQuery<WitnessPayload>("{\"code\":42}");
        AssertTrue(query.Decoded, "JSON decodes through the Decodable witness table");
        AssertTrue(query.RoundTrips, "re-encode and compare through the Encodable and Equatable witness tables");
    }

    public void TestQueryInitRejectsMalformedJson()
    {
        using var query = new WitnessReadingQuery<WitnessPayload>("{\"other\":1}");
        AssertFalse(query.Decoded, "missing key fails to decode");
        AssertFalse(query.RoundTrips, "no round trip without a decoded value");
    }
}
