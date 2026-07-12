// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Per-T projection of a generic-container property — the MusicKit
/// <c>MusicLibraryResponse&lt;T&gt;.items : MusicItemCollection&lt;T&gt;</c> shape.
/// <para>
/// The property type <c>TypedBag&lt;Item&gt;</c> mentions the parent generic
/// parameter, so on the open generic shell it resolves to
/// <c>TypedBag&lt;Swift.AnyType&gt;</c> and PropertyHandler skips it
/// (AnyTypeFallback) — the property is dead. The parent-CSM getter extension
/// projects it per conformer: <c>LibraryResponse&lt;AlbumItem&gt;.Items()</c>
/// returns a concretely-typed <c>TypedBag&lt;AlbumItem&gt;</c>. This test
/// round-trips the container's element count and per-element identity through
/// the closed getter for two independent conformers.
/// </para>
/// </summary>
public class TypedCollectionProjectionTests : TestBase
{
    public TypedCollectionProjectionTests(TestResults results) : base(results) { }

    public void TestAlbumResponse_Items_CountRoundTrips()
    {
        // The closed `Items()` getter must return a real, populated container.
        // Before the per-T property projection this getter did not exist at all
        // (the open-shell `items` was AnyTypeFallback-skipped), so a consumer
        // could not reach the response's items.
        using var resp = Functions.MakeAlbumLibraryResponse();
        using var bag = resp.Items();
        AssertEqual(3, (int)bag.Count(), "Items() must project a TypedBag<AlbumItem> of count 3");
    }

    public void TestAlbumResponse_Items_ElementIdentityRoundTrips()
    {
        // Element identity witnesses that the getter returns the ACTUAL backing
        // container (not an empty/default one): each element's itemId must match
        // what the factory stored, in order.
        using var resp = Functions.MakeAlbumLibraryResponse();
        using var bag = resp.Items();
        using var e0 = bag.Element(0);
        using var e2 = bag.Element(2);
        AssertEqual("a1", e0.ItemId, "Items() element[0] identity must round-trip");
        AssertEqual("a3", e2.ItemId, "Items() element[2] identity must round-trip");
    }

    public void TestSongResponse_Items_SecondConformerProjectsIndependently()
    {
        // The same property surface must project independently for a second
        // conformer — a per-conformer closed getter, not one hard-wired to the
        // first conformer's element type.
        using var resp = Functions.MakeSongLibraryResponse();
        using var bag = resp.Items();
        AssertEqual(1, (int)bag.Count(), "Second conformer: Items() must project a TypedBag<SongItem> of count 1");
        using var s0 = bag.Element(0);
        AssertEqual("s1", s0.ItemId, "Second conformer: Items() element[0] identity must round-trip");
    }
}
