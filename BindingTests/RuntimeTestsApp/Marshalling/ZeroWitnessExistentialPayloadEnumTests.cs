// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Enums/ZeroWitnessExistentialPayloadEnum.swift</c>.
/// Enum payloads typed <c>any Sendable</c> or bare <c>Any</c> carry no witness table and project to
/// <c>object</c>: extraction unboxes a copy of the contained value, and construction boxes the
/// argument. The labeled two-value case reads through the tuple path, the single-value cases
/// through the single-payload path.
/// </summary>
public class ZeroWitnessExistentialPayloadEnumTests : TestBase
{
    public ZeroWitnessExistentialPayloadEnumTests(TestResults results) : base(results) { }

    public void TestLabeledSendableIntPayloadExtracts()
    {
        using var payload = TestLibFunctions.MakeZeroWitnessAttribute("width", 42);
        AssertEqual(ZeroWitnessPayload.CaseTag.Attribute, payload.Tag, "Tag == Attribute");

        AssertTrue(payload.TryGetAttribute(out var name, out var value), "TryGetAttribute returns true");
        AssertEqual("width", name, "name round-trips");
        AssertEqual(42L, (long)value!, "any Sendable Int payload unboxes as long");
    }

    public void TestLabeledSendableStringPayloadRepeatedExtraction()
    {
        // A heap-allocated Swift string exercises the retain the enum copy takes on the payload:
        // each extraction must release it exactly once.
        var text = new string('x', 64);
        using var payload = TestLibFunctions.MakeZeroWitnessAttributeString("title", text);

        for (int i = 0; i < 16; i++)
        {
            AssertTrue(payload.TryGetAttribute(out var name, out var value), $"iter {i}: TryGetAttribute returns true");
            AssertEqual("title", name, $"iter {i}: name round-trips");
            AssertEqual(text, (string)value!, $"iter {i}: string payload round-trips");
        }
    }

    public void TestSingleSendablePayloadExtracts()
    {
        using var payload = TestLibFunctions.MakeZeroWitnessSendable(2.5);
        AssertEqual(ZeroWitnessPayload.CaseTag.Sendable, payload.Tag, "Tag == Sendable");

        AssertTrue(payload.TryGetSendable(out var value), "TryGetSendable returns true");
        AssertEqual(2.5, (double)value!, "any Sendable Double payload unboxes as double");
        AssertFalse(payload.TryGetAnything(out _), "TryGetAnything returns false on Sendable");
    }

    public void TestBareAnyPayloadExtracts()
    {
        var text = new string('y', 48);
        using var payload = TestLibFunctions.MakeZeroWitnessAnything(text);
        AssertEqual(ZeroWitnessPayload.CaseTag.Anything, payload.Tag, "Tag == Anything");

        AssertTrue(payload.TryGetAnything(out var value), "TryGetAnything returns true");
        AssertEqual(text, (string)value!, "Any String payload unboxes as string");
    }

    public void TestSingleSendablePayloadConstructsFromCSharp()
    {
        using var payload = ZeroWitnessPayload.Sendable(7L);
        AssertEqual("sendable(7)", TestLibFunctions.DescribeZeroWitnessPayload(payload), "Swift sees the boxed Int");
    }

    public void TestBareAnyPayloadConstructsFromCSharp()
    {
        var text = new string('z', 40);
        for (int i = 0; i < 8; i++)
        {
            using var payload = ZeroWitnessPayload.Anything(text);
            AssertEqual($"anything({text})", TestLibFunctions.DescribeZeroWitnessPayload(payload), $"iter {i}: Swift sees the boxed String");
            AssertTrue(payload.TryGetAnything(out var value), $"iter {i}: TryGetAnything returns true");
            AssertEqual(text, (string)value!, $"iter {i}: boxed String round-trips");
        }
    }
}
