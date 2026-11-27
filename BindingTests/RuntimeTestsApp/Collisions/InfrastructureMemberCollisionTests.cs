// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Swift members spelled like the members every projected type carries: <c>Payload</c> and
/// <c>Dispose()</c> on each owner, <c>PayloadBuffer</c> and the nested <c>Buffer</c> on a frozen
/// struct projected as a class. Each Swift member takes another C# name (a property the "Value"
/// suffix, a method the "Method" suffix), and every one is written and read back through Swift, so
/// a renamed accessor wired to the wrong field shows as a wrong value. Fixture:
/// Collisions/InfrastructureMemberCollisions.swift.
/// </summary>
public class InfrastructureMemberCollisionTests : TestBase
{
    public InfrastructureMemberCollisionTests(TestResults results) : base(results) { }

    public void TestABufferBackedStructKeepsEveryInfrastructureNamedField()
    {
        using var record = new InfraNamedFrozenRecord("first", 1, 2, 3);
        AssertEqual("first/1/2/3", record.Summary, "Swift reads what the C# constructor stored");

        record.PayloadValue = "second";
        record.PayloadBufferValue = 11;
        record.BufferValue = 12;
        record.DisposeValue = 13;
        AssertEqual("second/11/12/13", record.Summary, "each renamed setter writes its own Swift field");
        AssertEqual("second", record.PayloadValue, "payload reads back");
        AssertEqual(11, (int)record.PayloadBufferValue, "payloadBuffer reads back");
        AssertEqual(12, (int)record.BufferValue, "buffer reads back");
        AssertEqual(13, (int)record.DisposeValue, "dispose reads back");
        AssertEqual(1205, (int)record.BufferMethod(5), "buffer(at:) takes the method rename");
        AssertTrue(!record.Payload.IsInvalid, "the generated Payload handle is still the value's owner");
    }

    public void TestAResilientStructKeepsPayloadAndDispose()
    {
        using var record = new InfraNamedResilientRecord("opaque", 4);
        record.PayloadValue = "changed";
        record.DisposeValue = 5;
        AssertEqual("changed/5", record.Summary, "each renamed setter writes its own Swift field");
        AssertEqual("changed", record.PayloadValue, "payload reads back");
        AssertEqual("pre-changed", record.PayloadMethod("pre-"), "payload(prefix:) takes the method rename");
    }

    public void TestATwoIntFrozenStructKeepsPayloadAndDispose()
    {
        using var record = new InfraNamedValueRecord(21, 22);
        AssertEqual("21/22", record.Summary, "Swift reads what the C# constructor stored");
        record.PayloadValue = 31;
        record.DisposeValue = 32;
        AssertEqual("31/32", record.Summary, "each renamed setter writes its own Swift field");
        AssertEqual(31, record.PayloadValue, "payload reads back");
        AssertEqual(32, record.DisposeValue, "dispose reads back");
    }

    public void TestAClassKeepsPayloadAndDispose()
    {
        using var record = new InfraNamedRecordClass("class", 6);
        record.PayloadValue = "object";
        record.DisposeValue = 7;
        AssertEqual("object/7", record.Summary, "each renamed setter writes its own Swift field");
        AssertEqual("object", record.PayloadValue, "payload reads back");

        using var methods = new InfraNamedMethodClass();
        AssertEqual("x-method", methods.PayloadMethod("x"), "payload(prefix:) takes the method rename");
    }
}
