// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Regression coverage for Issue #33 — orphaned getter wrappers.
/// Reporter evidence: <c>GenerateContentResponse</c> in FirebaseAILogic 12.6 emitted the
/// private getter P/Invoke but dropped the public property body for Optional&lt;String&gt;,
/// Optional&lt;NonFrozenStruct&gt; and Array&lt;NonFrozenStruct&gt; getters on a non-frozen struct.
///
/// These three shapes — all on <c>OrphanedGetterParent</c> — exercise the same
/// <c>@_cdecl</c> property-wrapper code path that silently dropped in the reporter's build.
/// The tests assert the getters return the expected values (not that a property *exists*,
/// because a missing property would fail to compile — so compile-time success is itself
/// part of the regression).
/// </summary>
public class OrphanedGetterShapeTests : TestBase
{
    public OrphanedGetterShapeTests(TestResults results) : base(results) { }

    public void TestOptionalStringGetter_Some()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: "hello",
            metadataId: -1,
            metadataLabel: "",
            elementCount: 0);
        AssertEqual("hello", parent.Text, "Optional<String> getter — Some");
    }

    public void TestOptionalStringGetter_Nil()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: null,
            metadataId: -1,
            metadataLabel: "",
            elementCount: 0);
        AssertEqual(null, parent.Text, "Optional<String> getter — nil");
    }

    public void TestOptionalStructGetter_Some()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: null,
            metadataId: 7,
            metadataLabel: "seven",
            elementCount: 0);
        using var metadata = parent.Metadata;
        AssertNotNull(metadata, "Optional<NonFrozenStruct> getter — Some");
        AssertEqual(7, metadata!.Id, "metadata.id");
        AssertEqual("seven", metadata!.Label.ToString(), "metadata.label");
    }

    public void TestOptionalStructGetter_Nil()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: null,
            metadataId: -1,
            metadataLabel: "",
            elementCount: 0);
        using var metadata = parent.Metadata;
        AssertEqual(null, metadata, "Optional<NonFrozenStruct> getter — nil");
    }

    public void TestArrayOfStructGetter_NonEmpty()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: null,
            metadataId: -1,
            metadataLabel: "",
            elementCount: 3);
        var elements = parent.Elements;
        AssertEqual(3, elements.Count, "Array<NonFrozenStruct> getter — count");
        AssertEqual(0, elements[0].Id, "elements[0].id");
        AssertEqual("e0", elements[0].Label.ToString(), "elements[0].label");
        AssertEqual(2, elements[2].Id, "elements[2].id");
        AssertEqual("e2", elements[2].Label.ToString(), "elements[2].label");
    }

    public void TestArrayOfStructGetter_Empty()
    {
        using var parent = TestLibFunctions.MakeOrphanedGetterParent(
            text: null,
            metadataId: -1,
            metadataLabel: "",
            elementCount: 0);
        var elements = parent.Elements;
        AssertEqual(0, elements.Count, "Array<NonFrozenStruct> getter — empty");
    }
}
