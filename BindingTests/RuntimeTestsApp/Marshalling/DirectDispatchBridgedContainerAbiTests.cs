// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Round-trips ObjC-bridged containers — <c>[URL]</c>, <c>[String: URL]</c>,
/// <c>Set&lt;URL&gt;</c>, and the optional spellings of those — through the
/// generated <c>@_cdecl</c> wrappers on <see cref="DirectBridgedContainerHost"/>,
/// <see cref="DirectBridgedSlotHost"/>, and <see cref="DirectBridgedLookupHost"/>.
///
/// <para>Each member takes the container as an NSArray / NSDictionary / NSSet
/// handle at a <c>SBW_*</c> <c>CallConvCdecl</c> entry point. Tests construct or
/// call with a present container, the nil/null arm where the API is optional, and
/// the empty collection where that arm is natural, then assert the value Swift
/// returns: <see cref="DirectBridgedContainerHost.Stamp"/> after construction,
/// <c>count &amp;+ stamp</c> from the static methods, and the indexer payload
/// after a get/set cycle.</para>
/// </summary>
public class DirectDispatchBridgedContainerAbiTests : TestBase
{
    public DirectDispatchBridgedContainerAbiTests(TestResults results) : base(results) { }

    private static DirectBridgedContainerHost.BridgedMarker Stamp(int value)
        => new DirectBridgedContainerHost.BridgedMarker(value);

    private static Foundation.NSUrl Url(string s) => Foundation.NSUrl.FromString(s)!;

    public void TestDirectPathOptionalBridgedArrayInitializerRoundTrips()
    {
        // init(urls: [URL]?, stamp:) stores the array (nil → []) privately and exposes Stamp
        // plus a count read-back. The count is what separates the arms: the nil arm stores an
        // empty array, so a stamp-only assertion would pass on a wrapper that dropped the
        // present array's elements on the way in.
        var urls = new[] { Url("https://first.example.com/path"), Url("https://second.example.com/path") };
        using var present = new DirectBridgedContainerHost(urls, Stamp(1));
        AssertEqual(1, present.Stamp.Value,
            "[URL]? initializer preserves stamp for a present two-element array");
        AssertEqual(2, present.UrlCount,
            "both elements of the present array reach Swift's stored property");
        using var absent = new DirectBridgedContainerHost((IReadOnlyList<Foundation.NSUrl>?)null, Stamp(2));
        AssertEqual(2, absent.Stamp.Value,
            "[URL]? initializer preserves stamp for the nil arm");
        AssertEqual(0, absent.UrlCount, "the nil arm stores an empty array");
        using var empty = new DirectBridgedContainerHost(Array.Empty<Foundation.NSUrl>(), Stamp(11));
        AssertEqual(11, empty.Stamp.Value,
            "[URL]? initializer preserves stamp for an empty array");
        AssertEqual(0, empty.UrlCount, "the empty arm stores an empty array");
        TestLogger.Info("DirectBridgedContainerHost(urls:) round-tripped stamp and count on present, nil, and empty");
    }

    public void TestDirectPathOptionalBridgedDictionaryInitializerRoundTrips()
    {
        var lookup = new Dictionary<string, Foundation.NSUrl>
        {
            ["alpha"] = Url("https://alpha.example.com/path"),
        };
        using var present = new DirectBridgedContainerHost(lookup, Stamp(3));
        AssertEqual(3, present.Stamp.Value,
            "[String: URL]? initializer preserves stamp for a present one-entry dictionary");
        AssertEqual(1, present.LookupCount,
            "the present dictionary's entry reaches Swift's stored property");
        using var absent = new DirectBridgedContainerHost((IReadOnlyDictionary<string, Foundation.NSUrl>?)null, Stamp(4));
        AssertEqual(4, absent.Stamp.Value,
            "[String: URL]? initializer preserves stamp for the nil arm");
        AssertEqual(0, absent.LookupCount, "the nil arm stores an empty dictionary");
        using var empty = new DirectBridgedContainerHost(new Dictionary<string, Foundation.NSUrl>(), Stamp(12));
        AssertEqual(12, empty.Stamp.Value,
            "[String: URL]? initializer preserves stamp for an empty dictionary");
        AssertEqual(0, empty.LookupCount, "the empty arm stores an empty dictionary");
        TestLogger.Info("DirectBridgedContainerHost(lookup:) round-tripped stamp and count on present, nil, and empty");
    }

    public void TestDirectPathOptionalBridgedSetInitializerRoundTrips()
    {
        var unique = new HashSet<Foundation.NSUrl> { Url("https://one.example.com/path") };
        using var present = new DirectBridgedContainerHost(unique, Stamp(5));
        AssertEqual(5, present.Stamp.Value,
            "Set<URL>? initializer preserves stamp for a present one-element set");
        AssertEqual(1, present.UniqueCount,
            "the present set's element reaches Swift's stored property");
        using var absent = new DirectBridgedContainerHost((IReadOnlySet<Foundation.NSUrl>?)null, Stamp(6));
        AssertEqual(6, absent.Stamp.Value,
            "Set<URL>? initializer preserves stamp for the nil arm");
        AssertEqual(0, absent.UniqueCount, "the nil arm stores an empty set");
        using var empty = new DirectBridgedContainerHost(new HashSet<Foundation.NSUrl>(), Stamp(13));
        AssertEqual(13, empty.Stamp.Value,
            "Set<URL>? initializer preserves stamp for an empty set");
        AssertEqual(0, empty.UniqueCount, "the empty arm stores an empty set");
        TestLogger.Info("DirectBridgedContainerHost(unique:) round-tripped stamp and count on present, nil, and empty");
    }

    public void TestDirectPathBareBridgedArrayMethodRoundTrips()
    {
        // borrowedCount(_:stamp:) returns Int32(others.count) &+ stamp.value.
        var urls = new[] { Url("https://bare.example.com/path") };
        AssertEqual(8, DirectBridgedContainerHost.BorrowedCount(urls, Stamp(7)),
            "bare [URL] borrowedCount is 1 + stamp 7");
        AssertEqual(7, DirectBridgedContainerHost.BorrowedCount(Array.Empty<Foundation.NSUrl>(), Stamp(7)),
            "empty [URL] borrowedCount is 0 + stamp 7");
        TestLogger.Info("DirectBridgedContainerHost.BorrowedCount answered count + stamp for present and empty");
    }

    public void TestDirectPathBareBridgedSetMethodRoundTrips()
    {
        // borrowedUnique(_:stamp:) returns Int32(unique.count) &+ stamp.value.
        var unique = new HashSet<Foundation.NSUrl> { Url("https://bare-set.example.com/path") };
        AssertEqual(9, DirectBridgedContainerHost.BorrowedUnique(unique, Stamp(8)),
            "bare Set<URL> borrowedUnique is 1 + stamp 8");
        AssertEqual(8, DirectBridgedContainerHost.BorrowedUnique(new HashSet<Foundation.NSUrl>(), Stamp(8)),
            "empty Set<URL> borrowedUnique is 0 + stamp 8");
        TestLogger.Info("DirectBridgedContainerHost.BorrowedUnique answered count + stamp for present and empty");
    }

    public void TestDirectPathBareBridgedArraySubscriptRoundTrips()
    {
        // init(stamp:) stores []; the indexer get/set is the [URL] payload, keyed
        // by the nested frozen marker (the marker is not part of the stored value).
        using var slot = new DirectBridgedSlotHost(Stamp(9));
        AssertEqual(0, slot[Stamp(9)].Count,
            "[URL] subscript getter returns the empty array stored at init");

        var urls = new[] { Url("https://slot.example.com/path") };
        slot[Stamp(9)] = urls;
        var roundTripped = slot[Stamp(9)];
        AssertEqual(1, roundTripped.Count, "[URL] subscript setter stores one element");
        AssertEqual("https://slot.example.com/path", roundTripped[0]!.AbsoluteString,
            "[URL] subscript get after set returns the URL that went in");

        slot[Stamp(9)] = Array.Empty<Foundation.NSUrl>();
        AssertEqual(0, slot[Stamp(9)].Count,
            "[URL] subscript setter round-trips an empty array");
        TestLogger.Info("DirectBridgedSlotHost indexer round-tripped empty and present [URL]");
    }

    public void TestDirectPathBareBridgedDictionarySubscriptRoundTrips()
    {
        using var lookup = new DirectBridgedLookupHost(Stamp(10));
        AssertEqual(0, lookup[Stamp(10)].Count,
            "[String: URL] subscript getter returns the empty dictionary stored at init");

        lookup[Stamp(10)] = new Dictionary<string, Foundation.NSUrl>
        {
            ["k"] = Url("https://lookup.example.com/path"),
        };
        var roundTripped = lookup[Stamp(10)];
        AssertEqual(1, roundTripped.Count, "[String: URL] subscript setter stores one entry");
        AssertTrue(roundTripped.ContainsKey("k"), "[String: URL] subscript preserves the key that went in");
        AssertEqual("https://lookup.example.com/path", roundTripped["k"]!.AbsoluteString,
            "[String: URL] subscript get after set returns the URL that went in");

        lookup[Stamp(10)] = new Dictionary<string, Foundation.NSUrl>();
        AssertEqual(0, lookup[Stamp(10)].Count,
            "[String: URL] subscript setter round-trips an empty dictionary");
        TestLogger.Info("DirectBridgedLookupHost indexer round-tripped empty and present [String: URL]");
    }

    public void TestWrapperPathBareBridgedArrayStillBinds()
    {
        // liveCount(_:) has no frozen-struct sibling; it returns Int32(urls.count).
        var urls = new[] { Url("https://live.example.com/a"), Url("https://live.example.com/b"), Url("https://live.example.com/c") };
        AssertEqual(3, DirectBridgedContainerHost.LiveCount(urls),
            "bare [URL] liveCount returns the number of elements that went in");
        AssertEqual(0, DirectBridgedContainerHost.LiveCount(Array.Empty<Foundation.NSUrl>()),
            "empty [URL] liveCount returns 0");
        TestLogger.Info("DirectBridgedContainerHost.LiveCount answered the element count for present and empty");
    }
}
