// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic struct conforming to <c>Collection</c> where protocol-family
/// methods like <c>index(_:offsetBy:)</c> and <c>distance(from:to:)</c> have
/// pure <c>nint</c>-arithmetic signatures that never reference the parent's
/// generic parameter. The <c>signatureReferencesT</c> hard-gate in
/// <c>GenericDispatchEmitter.CanEmitStaticDispatch</c> is relaxed for
/// Collection-family conformers so these method wrappers emit at all — the
/// pre-fix shape left MusicKit's <c>MusicItemCollection&lt;TMusicItemType&gt;</c>
/// with four SB0001s.
///
/// Nint-only stored/computed properties (<c>startIndex</c>, <c>endIndex</c>)
/// and the T-returning array-backed projection property (<c>items: [Item]</c>)
/// are wrapped by the parallel relaxation in
/// <c>PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper</c> —
/// Collection conformers' concrete- AND simply-parameterized-T-returning
/// getters (bare <c>T</c>, <c>Array&lt;T&gt;</c>) are routed through
/// <c>@_cdecl</c> static dispatch wrappers rather than direct
/// <c>CallConvSwift</c> (which trips Mono Issue 1 <c>!ji->async</c> with 2+
/// type-metadata args AND SIGSEGVs on NativeAOT's multi-type-parameter
/// generic P/Invoke path). The <c>items</c>-backed collection-projection
/// members (<c>Count</c>, indexer, <c>GetEnumerator</c>) now round-trip on
/// both runtimes.
///
/// These tests prove the generated wrappers are (a) emitted at all, and
/// (b) round-trip integer arithmetic and array-backed projection through the
/// witness-table-backed dispatch path correctly.
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
    // Collection projection — array-backed path
    //
    // `Count`, indexer, and `GetEnumerator` route through the `Items` getter,
    // which is now emitted as a `@_cdecl` static dispatch wrapper
    // (`SBW_Get_SwiftBindingsTestLib_MusicItemBag_items`) rather than direct
    // `CallConvSwift` to the Swift mangled symbol. The wrapper renders the
    // `[Item]` return as `Swift.Array<Item>` inside the protocol extension
    // body and writes it through a `resultPtr`, avoiding both Mono Issue 1
    // (`!ji->async` on 2+ type-metadata args) and NativeAOT's
    // multi-type-parameter generic SIGSEGV.
    // ─────────────────────────────────────────────────────────────────────

    public void TestMusicItemBag_Count_ProjectsFromArrayBacking()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "one", secondId: "two", thirdId: "three");

        AssertEqual(3, bag.Count, "MusicItemBag.Count (array-backed projection)");
    }

    public void TestMusicItemBag_Indexer_ReturnsItemsByPosition()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "alpha", secondId: "beta", thirdId: "gamma");

        AssertEqual("alpha", bag[0].CollectibleId, "MusicItemBag[0].collectibleId");
        AssertEqual("beta", bag[1].CollectibleId, "MusicItemBag[1].collectibleId");
        AssertEqual("gamma", bag[2].CollectibleId, "MusicItemBag[2].collectibleId");
    }

    public void TestMusicItemBag_GetEnumerator_RoundTripsItems()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "one", secondId: "two", thirdId: "three");

        var seen = new List<string>();
        foreach (var item in bag)
        {
            seen.Add(item.CollectibleId);
        }
        AssertEqual(3, seen.Count, "MusicItemBag iteration count");
        AssertEqual("one", seen[0], "MusicItemBag iteration [0]");
        AssertEqual("two", seen[1], "MusicItemBag iteration [1]");
        AssertEqual("three", seen[2], "MusicItemBag iteration [2]");
    }

    public void TestMusicItemBag_OutOfRangeIndices_Throw()
    {
        // Bounds control for the ARRAY-BACKED projection: this indexer delegates
        // to the projected Swift.Array, which already raises
        // ArgumentOutOfRangeException, so an empty/negative/Count read must be a
        // catchable managed error here exactly as it is on the witness-backed path.
        using var empty = Functions.MakeEmptyMusicItemBag();
        IReadOnlyList<CollectibleCoin> emptyView = empty;
        AssertEqual(0, emptyView.Count, "empty MusicItemBag.Count");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { var _ = emptyView[0]; }, "empty bag[0] throws");

        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");
        IReadOnlyList<CollectibleCoin> view = bag;
        AssertThrows<ArgumentOutOfRangeException>(
            () => { var _ = view[-1]; }, "bag[-1] throws");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { var _ = view[view.Count]; }, "bag[Count] throws");
        AssertEqual("c", view[view.Count - 1].CollectibleId, "bag[Count - 1] still reads");
    }

    public void TestMusicItemBag_FormIndex_InoutOnGenericParent_DoesNotCrash()
    {
        // Round 6: `formIndex(_:offsetBy:)` takes `inout Int` on a generic struct parent.
        // Pre-fix: the inout-on-generic-parent hard gate in MethodWrapperEmitter and
        // WrapperValidation forced SB0001 + raw CallConvSwift, which the Collection
        // projection wrappers could not safely call. Post-fix: the static protocol
        // dispatch path threads UnsafeMutableRawPointer through the protocol boundary
        // and does the load/call/writeback inside the extension, so the generated
        // @_cdecl wrapper routes through CallConvCdecl with `ref nint i` and can be
        // called from Mono without the CallConvSwift ABI mismatch.
        //
        // The public C# API surfaces inout as `ref nint` (matches ParameterTests convention),
        // so the writeback is observable: formIndex(_:offsetBy:) sets `i = index(i, offsetBy:)`,
        // i.e. i += distance. The guarantee exercised here is the cross-boundary call completing
        // AND the inout index advancing correctly, with the wrapper's metadata + PWT plumbing
        // lined up for the concrete witness type.
        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");

        nint i = 1;
        bag.FormIndex(ref i, 2);
        AssertEqual(3, (int)i, "formIndex(offsetBy: 2) must advance the inout index 1 → 3");
        i = 0;
        bag.FormIndex(ref i, 3);
        AssertEqual(3, (int)i, "formIndex(offsetBy: 3) must advance the inout index 0 → 3");
        TestLogger.Info($"FormIndex(ref inout Int, Int) round-trip → i={i}");
    }
}
