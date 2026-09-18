// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Parameters/KeywordLabels.swift</c>.
/// Argument labels and enum-case payload labels that spell a C# reserved keyword are escaped to
/// verbatim identifiers (<c>object</c> -> <c>@object</c>). The scratch locals the marshalling paths
/// derive from those names must be spelled off the BARE name, because <c>@</c> is only legal as an
/// identifier's first character — but the values themselves must keep the escaped spelling, and a
/// derived local must still not collide with a real sibling parameter.
/// <para>These run the members rather than merely compiling them: a name that parses can still be
/// wired to the wrong slot, so every case round-trips its arguments through Swift and compares the
/// string Swift itself formats.</para>
/// </summary>
public class KeywordLabelTests : TestBase
{
    public KeywordLabelTests(TestResults results) : base(results) { }

    #region Enum payload labels

    public void TestKeywordLabelledBareAnyPayloadExtracts()
    {
        // `case failed(object: Any, reason: String)` — the zero-witness existential extraction,
        // whose container temp and witness-table metadata are both named after the payload label.
        using var payload = TestLibFunctions.MakeKeywordLabelFailed("timeout", 42);
        AssertEqual(KeywordLabelPayload.CaseTag.Failed, payload.Tag, "Tag == Failed");

        AssertTrue(payload.TryGetFailed(out var value, out var reason), "TryGetFailed returns true");
        // The existential arrives boxed as whatever Swift put in it (here the Swift Int32), so the
        // assertion is on the numeric value rather than on one particular boxed width.
        AssertEqual(42L, Convert.ToInt64(value!), "the Any payload under the keyword label unboxes");
        AssertEqual("timeout", reason, "the sibling payload round-trips");
    }

    public void TestKeywordLabelledBareAnyPayloadRepeatedExtraction()
    {
        // The metadata temp drives the value-witness Destroy that releases the enum copy's +1.
        // Repeating the extraction is what would show a mis-wired or skipped release.
        using var payload = TestLibFunctions.MakeKeywordLabelFailed(new string('r', 64), 7);

        for (int i = 0; i < 16; i++)
        {
            AssertTrue(payload.TryGetFailed(out var value, out var reason), $"iter {i}: TryGetFailed returns true");
            AssertEqual(7L, Convert.ToInt64(value!), $"iter {i}: payload round-trips");
            AssertEqual(new string('r', 64), reason, $"iter {i}: reason round-trips");
        }
    }

    public void TestKeywordLabelledContainerPayloadExtracts()
    {
        // `case listed(params: [String], checked: Bool)` — the container projection path, which
        // reads its raw marshalled value out of a temp named after the label.
        using var payload = TestLibFunctions.MakeKeywordLabelListed(new[] { "a", "b", "c" }, true);
        AssertEqual(KeywordLabelPayload.CaseTag.Listed, payload.Tag, "Tag == Listed");

        AssertTrue(payload.TryGetListed(out var items, out var flag), "TryGetListed returns true");
        AssertEqual(3, items!.Count, "array payload arrives with every element");
        AssertEqual("a", items[0], "element 0");
        AssertEqual("c", items[2], "element 2");
        AssertTrue(flag, "the Bool sibling round-trips");
    }

    public void TestKeywordLabelledDatePayloadExtracts()
    {
        // `case stamped(event: String, base: Date)` — the Date projection, which converts out of a
        // `double` temp named after the label.
        using var payload = TestLibFunctions.MakeKeywordLabelStamped("launch", 1000);
        AssertEqual(KeywordLabelPayload.CaseTag.Stamped, payload.Tag, "Tag == Stamped");

        AssertTrue(payload.TryGetStamped(out var name, out var when), "TryGetStamped returns true");
        AssertEqual("launch", name, "the String sibling round-trips");
        AssertEqual(1000L, (long)(when.ToUnixTimeMilliseconds() / 1000), "the Date payload converts from its raw temp");
    }

    public void TestKeywordLabelledPayloadConstructedFromCSharpIsSeenBySwift()
    {
        // The construction direction, checked by the language that owns the layout: Swift formats
        // the case it receives, so a payload written into the wrong slot shows up as wrong text.
        using var failed = KeywordLabelPayload.Failed(11L, "bad");
        AssertEqual("failed(11|bad)", TestLibFunctions.DescribeKeywordLabelPayload(failed),
            "Swift sees the keyword-labelled Any payload and its sibling");

        using var listed = KeywordLabelPayload.Listed(new[] { "x", "y" }, false);
        AssertEqual("listed(x,y|false)", TestLibFunctions.DescribeKeywordLabelPayload(listed),
            "Swift sees the keyword-labelled array payload");
    }

    #endregion

    #region Member parameter labels

    public void TestKeywordNamedParametersRoundTrip()
    {
        using var carrier = new KeywordLabelCarrier(5);

        AssertEqual("hi#3#5", carrier.Describe("hi", 3), "both keyword-named parameters reach Swift in order");
        AssertEqual("s[a,b]", carrier.Combine("s", new[] { "a", "b" }), "a keyword-named container parameter round-trips");
    }

    public void TestKeywordNamedParameterWithSiblingShadowingItsDerivedLocal()
    {
        // `pack(object: [String], objectBuffer: Int32)`. The container parameter spawns a buffer
        // local that a REAL sibling is already spelled like, so the escaped name is both de-escaped
        // (to be legal inside a compound identifier) and moved aside (so it does not redeclare the
        // sibling). Passing distinguishable values is what proves the two did not get crossed.
        using var carrier = new KeywordLabelCarrier(0);

        AssertEqual("p+q@9", carrier.Pack(new[] { "p", "q" }, 9),
            "the moved-aside container and the sibling that shadowed its buffer stay distinct");
    }

    public void TestKeywordNamedParameterWithSiblingShadowingItsConversionLocal()
    {
        // The same interaction one suffix over: `merge(object: String, objectSwift: String)`, where
        // the sibling is spelled like the String conversion local.
        using var carrier = new KeywordLabelCarrier(0);

        AssertEqual("left/right", carrier.Merge("left", "right"),
            "the keyword-named String and the sibling spelled like its conversion local stay distinct");
    }

    public void TestKeywordNamedSubscriptParameter()
    {
        // The subscript path renames its parameters positionally, so the keyword never reaches the
        // C# signature here — the assertion is that the member still binds and dispatches.
        using var carrier = new KeywordLabelCarrier(4);

        AssertEqual(7, carrier["abc"], "a subscript declared with a keyword label still dispatches");
    }

    public void TestKeywordNamedParametersOnAFreeFunction()
    {
        AssertEqual("h!a,b", TestLibFunctions.KeywordLabelJoin("h", new[] { "a", "b" }),
            "keyword-named labels round-trip off the free-function path too");
    }

    #endregion
}
