// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage: a raw-value enum that conforms to a protocol and is used as a
/// generic argument at a marker-constrained position (<c>where T : Sendable</c>) must be
/// demoted to a class so it can implement the protocol's projected interface.
///
/// The parser drops the module-qualified marker (<c>Swift.Sendable</c>) as an
/// unrepresentable nominal conformance. That drop once erased the only signal the
/// enum-demotion gate keyed off, so the enum regressed to a bare C# enum — which cannot
/// implement an interface, so the protocol conformance silently vanished. These tests
/// assert the enum is class-projected, implements the interface, and round-trips through
/// the marker-constrained generic container.
/// </summary>
public class DemotedEnumProtocolTests : TestBase
{
    public DemotedEnumProtocolTests(TestResults results) : base(results) { }

    public void TestDemotedEnumImplementsInterface()
    {
        // A bare C# enum cannot implement an interface; this only holds for the class projection.
        AssertTrue(typeof(ITagValueProviding).IsAssignableFrom(typeof(AlertKind)),
            "AlertKind is class-projected and implements ITagValueProviding");
    }

    public void TestCaseAccessorTag()
    {
        var warning = AlertKind.Warning; // cached singleton — no disposal
        AssertEqual(AlertKind.CaseTag.Warning, warning.Tag, "Warning.Tag == Warning");
    }

    public void TestProtocolMemberValue()
    {
        var critical = AlertKind.Critical;
        AssertEqual(2, critical.TagValue, "Critical.TagValue == 2 (rawValue)");
    }

    public void TestProtocolDispatchThroughInterface()
    {
        ITagValueProviding provider = AlertKind.Info;
        AssertEqual(0, provider.TagValue, "Info dispatched via interface == 0");
    }

    public void TestRoundTripThroughMarkerConstrainedGeneric()
    {
        using var carrier = new AlertCarrier(kind: AlertKind.Warning);
        using var box = carrier.Boxed;
        using var item = box.Item;
        AssertEqual(AlertKind.CaseTag.Warning, item.Tag, "round-tripped enum case preserved");
        AssertEqual(1, item.TagValue, "round-tripped protocol member preserved");
    }
}
