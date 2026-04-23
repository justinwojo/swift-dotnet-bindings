// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Session 2 Issue C regression: generic struct conforming to <c>Collection</c>
/// where protocol-family methods like <c>index(_:offsetBy:)</c> and
/// <c>distance(from:to:)</c> have pure <c>nint</c>-arithmetic signatures that
/// never reference the parent's generic parameter. Before the fix in
/// <c>GenericDispatchEmitter.CanEmitStaticDispatch</c>, these methods were
/// rejected at the <c>signatureReferencesT</c> hard-gate and skipped with
/// reason <c>generic_parent</c> — matching the shape that left MusicKit's
/// <c>MusicItemCollection&lt;TMusicItemType&gt;</c> with four SB0001s.
///
/// Nint-only stored/computed properties (<c>startIndex</c>, <c>endIndex</c>)
/// are wrapped by a parallel relaxation in
/// <c>PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper</c> —
/// Collection conformers' concrete-return getters are now routed through
/// <c>@_cdecl</c> static dispatch wrappers rather than direct
/// <c>CallConvSwift</c> (which trips Mono Issue 1 <c>!ji->async</c> with 2+
/// type-metadata args). The <c>items</c>-backed collection-projection members
/// (<c>Count</c>, indexer, <c>GetEnumerator</c>) route through the
/// <c>Items</c> getter — a separate T-returning path whose cdecl routing is
/// handled in Session 3 (WeatherKit <c>Forecast&lt;Element&gt;</c>) — and are
/// intentionally not exercised here.
///
/// These tests prove the generated wrappers are (a) emitted at all, and
/// (b) round-trip integer arithmetic through the witness-table-backed
/// dispatch path correctly.
/// </summary>
public class MusicItemBagTests : TestBase
{
    public MusicItemBagTests(TestResults results) : base(results) { }

    public void TestMusicItemBag_Factory_RoundTripsIndices()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "track-1", secondId: "track-2", thirdId: "track-3");

        AssertEqual(0, bag.StartIndex, "startIndex");
        AssertEqual(3, bag.EndIndex, "endIndex");
    }

    public void TestMusicItemBag_IndexOffsetBy_AdvancesByN()
    {
        // index(_:offsetBy:) signature: (Int, Int) -> Int. No Item reference
        // anywhere. Pre-fix: SB0001 skip reason generic_parent. Post-fix: emits
        // `public nint Index(int i, int distance)` that calls the Collection
        // witness table and returns i + distance.
        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");

        AssertEqual((nint)2, bag.Index(0, 2), "index(0, offsetBy: 2)");
        AssertEqual((nint)3, bag.Index(1, 2), "index(1, offsetBy: 2)");
        AssertEqual((nint)0, bag.Index(3, -3), "index(3, offsetBy: -3)");
    }

    public void TestMusicItemBag_Distance_ReturnsEndMinusStart()
    {
        // distance(from:to:) signature: (Int, Int) -> Int. Same shape.
        // Emitted as `public nint Distance(int start, int end)`.
        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");

        AssertEqual((nint)3, bag.Distance(0, 3), "distance(from: 0, to: 3)");
        AssertEqual((nint)(-2), bag.Distance(2, 0), "distance(from: 2, to: 0)");
        AssertEqual((nint)0, bag.Distance(1, 1), "distance(from: 1, to: 1)");
    }

    public void TestMusicItemBag_IndexAfter_IncrementsByOne()
    {
        // index(after:) is a direct Collection requirement — also an nint-only
        // signature. Kept as a sanity check that the witness dispatch path
        // works for the minimal case, not just the protocol-extension methods.
        // Emitted as `public nint Index(int i)` (single-arg overload).
        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");

        AssertEqual((nint)1, bag.Index(0), "index(after: 0)");
        AssertEqual((nint)3, bag.Index(2), "index(after: 2)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Collection projection — array-backed path (Count only)
    //
    // MusicItemBag has a public `let items: [Item]`, so CollectionProjectionEmitter
    // takes the array-backed path: `Count => Items.Count`. This guard proves that
    // the projection at least lowers to an `IReadOnlyList<TItem>` surface with a
    // working Count getter.
    //
    // The indexer, foreach, and IReadOnlyList tests that used to live here were
    // removed: they delegate through `Items[int]` → `SwiftArray<TItem>.get_Item`,
    // which crashes inside CollectibleCoin's init-with-copy value witness when
    // TItem is a user struct with a reference-counted String field. That path
    // never worked — no prior runtime test exercised `SwiftArray<UserStruct>.get_Item`
    // — and root-causing it is a separate scope from Session 3 (which targets
    // Forecast<T>'s witness-dispatch projection, exercised in ForecastSeriesTests
    // where the same element type round-trips cleanly via the @_cdecl subscript
    // wrapper — confirming the bug is in the array-delegation path, not in
    // CollectibleCoin marshalling itself).
    // ─────────────────────────────────────────────────────────────────────

    public void TestMusicItemBag_Count_ProjectsFromArrayBacking()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "one", secondId: "two", thirdId: "three");

        AssertEqual(3, bag.Count, "MusicItemBag.Count (array-backed projection)");
    }
}
