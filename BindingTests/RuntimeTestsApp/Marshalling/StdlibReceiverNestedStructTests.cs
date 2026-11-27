// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using NestedIn = SwiftBindingsTestLib.Index;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Structs the test library declares inside an extension of <c>String.Index</c>, whose own metadata
/// accessor the generator cannot read. The structs bind under a C# host named for the receiver, and
/// a holder storing one keeps Swift's calling convention: the frozen payload's holder crosses in
/// registers, the resilient payload's holder through memory. Swift names each stored word, so a
/// crossing in the wrong convention reads as a wrong value. Fixture:
/// CrossModule/StdlibReceiverNestedStructs.swift.
/// </summary>
public class StdlibReceiverNestedStructTests : TestBase
{
    public StdlibReceiverNestedStructTests(TestResults results) : base(results) { }

    public void TestAHolderOfTheFrozenNestedPayloadCrossesInRegisters()
    {
        var holder = new StdlibNestedFrozenPayloadHolder(new NestedIn.StdlibNestedFrozenPayload(3, 4), 5);
        AssertEqual(30405, (long)TestLibFunctions.StdlibNestedFrozenHolderDigest(holder), "Swift reads a C#-built holder");

        var fromSwift = TestLibFunctions.MakeStdlibNestedFrozenHolder(6, 7, 8);
        AssertEqual(6, (long)fromSwift.Inner.First, "C# reads the returned payload's first word");
        AssertEqual(7, (long)fromSwift.Inner.Second, "C# reads the returned payload's second word");
        AssertEqual(8, (long)fromSwift.Tag, "C# reads the returned tag");
        AssertEqual(60708, (long)TestLibFunctions.StdlibNestedFrozenHolderDigest(fromSwift), "Swift reads its own holder back");
    }

    public void TestAHolderOfTheResilientNestedPayloadCrossesThroughMemory()
    {
        using var payload = new NestedIn.StdlibNestedResilientPayload(9);
        AssertEqual(9, (long)payload.Value, "C# reads the resilient payload");
        using var holder = new StdlibNestedResilientPayloadHolder(payload, 2);
        AssertEqual(902, (long)TestLibFunctions.StdlibNestedResilientHolderDigest(holder), "Swift reads a C#-built holder");
    }
}
