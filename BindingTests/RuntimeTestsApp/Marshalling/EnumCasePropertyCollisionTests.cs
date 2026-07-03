// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// FB-1 regression coverage: a Swift enum (<see cref="ShareSource"/>) declares associated-value
/// cases (<c>.image</c>/<c>.link</c>/<c>.blob</c>) alongside computed properties of the same name.
/// Both the case constructor and the property project to the same C# identifier; the property used
/// to be dropped as a <c>DuplicateSignature</c>. The generator now recovers it by disambiguating
/// the property side with a <c>Value</c> suffix (<c>Image</c> → <c>ImageValue</c>) while the case
/// constructor keeps the bare name. This asserts both surface and round-trip.
///
/// Cosmetic (C#-name-only) rename with no ABI/@_cdecl impact, so the simulator gate is sufficient.
/// </summary>
public class EnumCasePropertyCollisionTests : TestBase
{
    public EnumCasePropertyCollisionTests(TestResults results) : base(results) { }

    public void TestImageCaseConstructorAndRenamedProperty()
    {
        // Case constructor keeps the bare name; the colliding property is recovered as ImageValue.
        using var src = ShareSource.Image(42);
        AssertEqual(ShareSource.CaseTag.Image, src.Tag, "Tag == Image");
        AssertEqual(42, src.ImageValue, "ImageValue reads the .image associated value");
    }

    public void TestLinkCaseConstructorAndRenamedProperty()
    {
        using var src = ShareSource.Link("photos.example");
        AssertEqual(ShareSource.CaseTag.Link, src.Tag, "Tag == Link");
        AssertEqual("photos.example", src.LinkValue.ToString(), "LinkValue reads the .link associated value");
    }

    public void TestBlobCaseConstructorAndRenamedProperty()
    {
        using var src = ShareSource.Blob(7);
        AssertEqual(ShareSource.CaseTag.Blob, src.Tag, "Tag == Blob");
        AssertEqual(7, src.BlobValue, "BlobValue reads the .blob associated value");
    }

    public void TestPropertyFallbackWhenCaseDoesNotMatch()
    {
        // A recovered accessor returns its Swift-defined fallback when the case does NOT match, and
        // the real associated value when it does — proving it dispatches to Swift, not a no-op stub.
        using var link = ShareSource.Link("not-an-image");
        AssertEqual(0, link.ImageValue, "ImageValue is 0 (fallback) for a .link case");
        AssertEqual(-1, link.BlobValue, "BlobValue is -1 (fallback) for a .link case");
        AssertEqual("not-an-image", link.LinkValue.ToString(), "LinkValue reads the .link associated text");

        using var blob = ShareSource.Blob(99);
        AssertEqual("", blob.LinkValue.ToString(), "LinkValue is empty (fallback) for a .blob case");
        AssertEqual(99, blob.BlobValue, "BlobValue reads the .blob value");
    }

    public void TestTryGetStillWorksAlongsideRenamedProperty()
    {
        // The case-inspection helper (TryGet<Case>) coexists with the recovered property.
        using var src = ShareSource.Image(123);
        AssertTrue(src.TryGetImage(out var value), "TryGetImage returns true for an .image case");
        AssertEqual(123, value, "TryGetImage yields the associated value");
    }
}
