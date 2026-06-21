// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end ABI gate for the convergence of <c>WitnessDispatchEmitter.IsOptionalClassReturn</c>
/// onto the canonical oracle <c>WrapperValidation.IsOptionalWithReferenceInner</c>. Before the
/// convergence, an <c>Optional&lt;reference&gt;</c> protocol property getter was emitted only when the
/// inner type was a pure-Swift class (<c>IsSwiftClassType</c>); for an Apple ObjC class
/// (<c>NSData</c>) or an ObjC-bridgeable value type (<c>URL</c> → <c>NSURL</c>) the proxy member
/// carried an SB0003 <c>[Obsolete]</c> and its body threw <see cref="System.NotSupportedException"/>.
/// The oracle recognises both as nullable-pointer-ABI references, so the generator now emits a real
/// witness accessor for each.
///
/// The probe is vended as <c>any OptionalReferenceWitnessProbe</c>, so every property read here goes
/// through the Swift-backed existential's witness table — the exact path SB0003 used to reject, and
/// the shape the real-world libraries (BlinkID <c>bundleURL</c>/<c>uiImage</c>, Kingfisher, RichTextKit)
/// exercise. Each property is pinned on both the non-nil round-trip and the nil sentinel.
/// </summary>
public class OptionalReferenceWitnessReturnTests : TestBase
{
    public OptionalReferenceWitnessReturnTests(TestResults results) : base(results) { }

    #region fileURL — ObjC-bridgeable value type (URL → NSURL) via witness dispatch

    public void TestFileUrlNonNilRoundTrips()
    {
        var vendor = new OptionalReferenceWitnessVendor();
        var probe = vendor.MakeProbe("https://example.com/report.pdf", -1);
        NSUrl? url = probe.FileURL;
        AssertNotNull(url, "fileURL (present) must materialise an NSUrl through witness dispatch");
        AssertEqual("https://example.com/report.pdf", url!.AbsoluteString, "fileURL round-trip");
        TestLogger.Info("Optional URL? witness getter round-tripped through the existential");
    }

    public void TestFileUrlNilSurfacesNull()
    {
        var vendor = new OptionalReferenceWitnessVendor();
        var probe = vendor.MakeProbe(null, -1);
        AssertNull(probe.FileURL, "fileURL (absent) must surface null, not a sentinel pointer");
    }

    #endregion

    #region attachment — Apple ObjC class (NSData) via witness dispatch

    public void TestAttachmentNonNilRoundTrips()
    {
        var vendor = new OptionalReferenceWitnessVendor();
        var probe = vendor.MakeProbe(null, 5);
        NSData? data = probe.Attachment;
        AssertNotNull(data, "attachment (present) must materialise an NSData through witness dispatch");
        AssertEqual(5, (int)data!.Length, "attachment byte length round-trip");
        TestLogger.Info("Optional NSData? witness getter round-tripped through the existential");
    }

    public void TestAttachmentNilSurfacesNull()
    {
        var vendor = new OptionalReferenceWitnessVendor();
        var probe = vendor.MakeProbe(null, -1);
        AssertNull(probe.Attachment, "attachment (absent) must surface null, not a sentinel pointer");
    }

    #endregion
}
