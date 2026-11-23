// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Payload cases whose tuple holds a simple enum, and a case named like the generated class's
/// <c>Payload</c> handle. Swift lays a raw-value enum inside a payload tuple out as its case tag
/// and an @objc enum as its raw value, so the byte after it sits where only the enum's own layout
/// puts it. Fixture: Enums/EnumPayloadEdgeCases.swift.
/// </summary>
public class EnumPayloadEdgeCaseTests : TestBase
{
    public EnumPayloadEdgeCaseTests(TestResults results) : base(results) { }

    public void TestIntRawValueEnumInPayloadTuple()
    {
        using var value = TestLibFunctions.GetRawTuplePayloadRaw();
        AssertTrue(value.TryGetRaw(out var e, out var b), "the raw case");
        AssertEqual(AsyncTupleRaw.High, e, "the enum element");
        AssertEqual((byte)42, b, "the byte after the enum");
        using var built = RawTuplePayload.Raw(AsyncTupleRaw.Mid, 5);
        AssertEqual("raw:7:5", TestLibFunctions.RawTuplePayloadName(built), "a payload built in C#");
    }

    public void TestByteRawValueEnumInPayloadTuple()
    {
        using var value = TestLibFunctions.GetRawTuplePayloadSmall();
        AssertTrue(value.TryGetSmall(out var e, out var b), "the small case");
        AssertEqual(AsyncTupleSmall.Big, e, "the enum element");
        AssertEqual((byte)43, b, "the byte after the enum");
        using var built = RawTuplePayload.Small(AsyncTupleSmall.Tiny, 6);
        AssertEqual("small:1:6", TestLibFunctions.RawTuplePayloadName(built), "a payload built in C#");
    }

    public void TestObjCEnumInPayloadTuple()
    {
        using var value = TestLibFunctions.GetRawTuplePayloadObjC();
        AssertTrue(value.TryGetObjc(out var e, out var b), "the objc case");
        AssertEqual(AuthErrorCodeLike.UserNotFound, e, "the enum element");
        AssertEqual((byte)44, b, "the byte after the enum");
        using var built = RawTuplePayload.Objc(AuthErrorCodeLike.WrongPassword, 7);
        AssertEqual("objc:17009:7", TestLibFunctions.RawTuplePayloadName(built), "a payload built in C#");
    }

    public void TestCaseNamedPayloadKeepsTheHandle()
    {
        using var made = TestLibFunctions.PayloadNamedCaseMake(11);
        AssertTrue(made.TryGetPayload(out var n), "the payload case");
        AssertEqual((nint)11, n, "its value");
        AssertTrue(!made.Payload.IsInvalid, "the generated handle is still the Payload member");
        using var built = PayloadNamedCase.PayloadValue(12);
        AssertEqual("payload:12", TestLibFunctions.PayloadNamedCaseName(built), "the renamed factory");
        AssertEqual(PayloadNamedCase.CaseTag.Payload, built.Tag, "the case tag keeps the Swift case name");
    }
}
