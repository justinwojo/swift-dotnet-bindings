// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Case-only collisions, end to end. Swift identifiers are case-sensitive and its libraries use
/// that freely; C# folds the difference away, so each shape below would otherwise cost a
/// declaration its binding or produce two members a reader cannot tell apart.
///
/// Generated shapes verified against the bindings:
///   - <c>EndpointSettings</c> — Swift <c>url</c>/<c>URL</c> → <c>Url</c>/<c>Url2</c>. Declaration
///     order decides the winner, so the renamed one must still read the SECOND Swift field.
///   - <c>ScanKit</c> (member-free container with a nested type) emits as a C# NAMESPACE, and the
///     sibling class <c>SCANKit</c> → <c>SCANKitInfo</c>. Both must be reachable.
///   - <c>EndpointDescribing</c> / <c>ReversedEndpoint</c> — the conformer declares the same two
///     requirements in the opposite order and must still bind each to the storage the protocol
///     named, or the interface silently reads the wrong field.
///   - <c>TransferRecord.Status</c> → <c>StatusKind</c> — the enum arm of the nested-type rename.
///   - <c>Checksum.checksum()</c> → <c>GetChecksum()</c> — a method may not share its enclosing
///     type's name (CS0542).
///
/// The rename is only correct if the disambiguated member is wired to the RIGHT Swift storage,
/// which is what these assert — a name that compiles but reads the wrong field would pass a
/// compile gate and fail a consumer.
/// </summary>
public class CaseOnlyCollisionTests : TestBase
{
    public CaseOnlyCollisionTests(TestResults results) : base(results) { }

    public void TestCaseOnlySiblingPropertiesBothBindToTheirOwnStorage()
    {
        using var settings = new EndpointSettings("lower", "UPPER");

        // Declaration order decides: Swift `url` keeps the natural projection.
        AssertEqual("lower", settings.Url, "Url reads Swift `url`");
        // …and the later `URL` is the one that moved. If the two were crossed, both asserts
        // could not hold at once.
        AssertEqual("UPPER", settings.Url2, "Url2 reads Swift `URL`");
    }

    public void TestNamespaceFacadeAndCaseCollidingSiblingBothUsable()
    {
        // The facade keeps its name and stays a namespace — its nested type resolves under it.
        // Spelled out in full because a `using` directive imports a namespace's types, not its
        // nested namespaces, and `ScanKit` is now a namespace rather than a type.
        using var region = new SwiftBindingsTestLib.ScanKit.Region(3, 4);
        AssertEqual(12, region.GetArea(), "ScanKit.Region resolves under the facade namespace");

        // The sibling that case-folds onto the facade took the aggregate suffix.
        using var kit = new SCANKitInfo();
        AssertEqual("SCANKit", kit.GetDescribe(), "SCANKitInfo is the renamed sibling class");
    }

    public void TestConformerAdoptsTheProtocolsCaseOnlyNamesForItsOwnStorage()
    {
        // `ReversedEndpoint` declares `URL` before `url` — the opposite of the protocol. Reading
        // through the INTERFACE is the assertion that matters: C# binds an implicit interface
        // implementation by name, so a conformer that had picked its own winner would compile
        // and hand back the other field.
        using var endpoint = new ReversedEndpoint("lower", "UPPER");
        IEndpointDescribing described = endpoint;

        AssertEqual("lower", described.Url, "IEndpointDescribing.Url reads Swift `url`");
        AssertEqual("UPPER", described.Url2, "IEndpointDescribing.Url2 reads Swift `URL`");
        AssertEqual("lower", endpoint.Url, "the concrete member agrees with the interface");
        AssertEqual("UPPER", endpoint.Url2, "the concrete member agrees with the interface");
    }

    public void TestNestedEnumTakesTheKindSuffixAndRoundTrips()
    {
        using var record = new TransferRecord(TransferRecord.StatusKind.Settled, 250);

        AssertEqual(TransferRecord.StatusKind.Settled, record.Status, "property keeps its natural name");
        AssertEqual(250, record.Amount, "sibling property unaffected");
        AssertTrue(record.IsSettled(), "renamed enum round-trips into Swift");
    }

    public void TestMethodNamedForItsEnclosingTypeTakesTheGetPrefix()
    {
        using var checksum = new Checksum(99);

        AssertEqual(99, checksum.Value, "property keeps its natural name");
        AssertEqual(99, checksum.GetChecksum(), "checksum() is reachable as GetChecksum()");
    }
}
