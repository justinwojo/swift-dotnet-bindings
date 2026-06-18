// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// End-to-end gate for the GSF (generic-static-factory) constructor path emitting a parent-
/// generic-param <c>where</c> clause (<c>WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere</c>).
///
/// The fixture <c>MarkerCtorBox&lt;Value&gt;</c> has a constructor-added STDLIB MARKER constraint:
/// <c>init(sendableCount:)</c> lives in <c>extension MarkerCtorBox where Value: Sendable</c>. That
/// marker is the only conformance shape that reaches the helper's conformance branch in practice,
/// and it MUST be dropped — Swift forbids a non-marker protocol's conditional conformance from
/// depending on a marker, so emitting <c>where Value: Sendable</c> on the <c>_SBW_GSF</c> extension
/// fails <c>swiftc</c>, strips the wrapper, and leaves this constructor dangling. Reaching these
/// assertions at all is the headline: it proves the wrapper compiled (marker dropped → unconditional
/// conformance) and the now-unconditional erased dispatch round-trips.
///
/// Tag semantics: <c>init(sendableCount: n)</c> stores <c>"sendable-{n}"</c>.
/// </summary>
public class MarkerConstrainedCtorTests : TestBase
{
    public MarkerConstrainedCtorTests(TestResults results) : base(results) { }

    public void TestMarkerConstrainedInit_RoundTripsTag()
    {
        using var box = new MarkerCtorBox<CtorAdmIntValue>((nint)5);
        AssertEqual("sendable-5", box.Tag,
            "marker-constrained (sendableCount:) GSF init round-trips through unconditional erased dispatch");
    }

    public void TestBaseInit_RoundTripsTag()
    {
        using var box = new MarkerCtorBox<CtorAdmIntValue>("base-tag");
        AssertEqual("base-tag", box.Tag, "base (tag:) GSF init round-trips");
    }

    public void TestBothConstructorsEmitted()
    {
        // The marker-constrained init must survive as a real surface (its wrapper compiled),
        // alongside the unconstrained base init.
        var boxType = typeof(MarkerCtorBox<CtorAdmIntValue>);

        AssertNotNull(
            boxType.GetConstructor(new[] { typeof(string) }),
            "base (string tag) init is emitted");
        AssertNotNull(
            boxType.GetConstructor(new[] { typeof(nint) }),
            "marker-constrained (nint sendableCount) init is emitted (wrapper compiled → marker dropped)");
    }
}
