// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Regression coverage for Issue #33 — orphaned getter wrappers.
/// Reporter evidence: a class with Optional&lt;String&gt;,
/// Optional&lt;NonFrozenStruct&gt; and Array&lt;NonFrozenStruct&gt; getters on a non-frozen struct
/// emitted the private getter P/Invoke but dropped the public property body.
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

    /// Regression test for IEnumerable&lt;NonFrozenStruct&gt; raw-IntPtr packing in the C#→Swift parameter direction.
    /// Exercises the C#→Swift parameter direction for <c>IEnumerable&lt;NonFrozenStruct&gt;</c>:
    /// the Swift wrapper reinterprets the buffer as <c>Array&lt;OrphanedGetterElement&gt;</c>,
    /// so the C# side must pack each element's payload bytes by value via
    /// <c>SwiftArray&lt;OrphanedGetterElement&gt;.FromEnumerable</c> + <c>MarshalToSwift</c>.
    /// Pre-fix the generator emitted <c>SwiftArray&lt;IntPtr&gt;.FromEnumerable(...Select(e =&gt;
    /// e.Payload.DangerousGetHandle()))</c>, packing 1-word handle pointers per slot —
    /// an ABI mismatch against the Swift-side struct-by-value layout. Round-tripping
    /// the C#-built array back through the <c>.elements</c> getter is the structural
    /// assertion that the buffer was packed correctly.
    public void TestArrayOfNonFrozenStruct_ConstructorRoundTrip()
    {
        using var elem0 = new OrphanedGetterElement(10, "alpha");
        using var elem1 = new OrphanedGetterElement(20, "beta");
        using var elem2 = new OrphanedGetterElement(30, "gamma");

        using var parent = new OrphanedGetterParent(
            text: null,
            metadata: null,
            elements: new[] { elem0, elem1, elem2 });

        var elements = parent.Elements;
        AssertEqual(3, elements.Count,
            "C#-built IEnumerable<OrphanedGetterElement> round-trips count");
        AssertEqual(10, elements[0].Id, "elements[0].id round-trips by value");
        AssertEqual("alpha", elements[0].Label.ToString(),
            "elements[0].label round-trips by value");
        AssertEqual(20, elements[1].Id, "elements[1].id round-trips by value");
        AssertEqual("beta", elements[1].Label.ToString(),
            "elements[1].label round-trips by value");
        AssertEqual(30, elements[2].Id, "elements[2].id round-trips by value");
        AssertEqual("gamma", elements[2].Label.ToString(),
            "elements[2].label round-trips by value");
    }

    /// Single-element variant so the per-slot byte layout is still validated even
    /// when array growth/multi-slot copy semantics aren't exercised.
    public void TestArrayOfNonFrozenStruct_SingleElement()
    {
        using var only = new OrphanedGetterElement(99, "solo");
        using var parent = new OrphanedGetterParent(
            text: null,
            metadata: null,
            elements: new[] { only });

        var elements = parent.Elements;
        AssertEqual(1, elements.Count, "Single-element non-frozen struct array round-trips");
        AssertEqual(99, elements[0].Id, "elements[0].id round-trips");
        AssertEqual("solo", elements[0].Label.ToString(), "elements[0].label round-trips");
    }

    /// Empty IEnumerable input — exercises the zero-element fast path
    /// (SwiftArray.AppendRange returns early; no MarshalToSwift dispatch).
    public void TestArrayOfNonFrozenStruct_EmptyConstructorRoundTrip()
    {
        using var parent = new OrphanedGetterParent(
            text: null,
            metadata: null,
            elements: System.Array.Empty<OrphanedGetterElement>());

        var elements = parent.Elements;
        AssertEqual(0, elements.Count,
            "Empty IEnumerable<OrphanedGetterElement> round-trips as empty array");
    }
}
