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
/// <c>Items</c> getter — a separate T-returning path that is still emitted
/// as raw <c>CallConvSwift</c> and trips upstream issue #4. The Count test
/// below carries a <c>[Skip]</c> with that reason; the cdecl routing for
/// T-returning generic property getters is Session 3 (WeatherKit
/// <c>Forecast&lt;Element&gt;</c>) generator work.
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
    // Collection projection — array-backed path
    //
    // `Count`, indexer, and `GetEnumerator` route through the `Items` getter,
    // which is currently emitted as raw `CallConvSwift` to the Swift mangled
    // symbol with two generic-metadata args (TItemMetadata + TItemCollectibleItemPWT).
    // That trips upstream issue #4 — same pattern as
    // `BasicGenericTests.TestGetPairSameType`. The destroy witness then
    // SIGSEGVs on dispose because the bag's payload is left in a torn state.
    //
    // Routing T-returning generic struct property getters through `@_cdecl`
    // wrappers is Session 3 (WeatherKit `Forecast<Element>`) generator work
    // and out of scope here. Skipped on both runtimes to match the bug's
    // documented blast radius.
    // ─────────────────────────────────────────────────────────────────────

    [Skip("Upstream issue #4: multi-type-parameter generic SIGSEGV. The MusicItemBag.items getter is emitted as direct CallConvSwift with 2 type metadata params (TItem + TItem:CollectibleItem PWT), which crashes on both Mono and NativeAOT. Long-term fix: PropertyWrapperEmitter needs to route T-returning generic struct property getters through @_cdecl wrappers.")]
    public void TestMusicItemBag_Count_ProjectsFromArrayBacking()
    {
        using var bag = Functions.MakeMusicItemBag(
            firstId: "one", secondId: "two", thirdId: "three");

        AssertEqual(3, bag.Count, "MusicItemBag.Count (array-backed projection)");
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
        // The public C# API hides inout (matches ParameterTests.TestIncrementValue /
        // TestSwapValues convention — writeback semantics aren't surfaced at the API
        // level, only at the P/Invoke). The guarantee exercised here is: the
        // cross-boundary call completes without crashing and the wrapper's metadata +
        // PWT plumbing still lines up for the concrete witness type.
        using var bag = Functions.MakeMusicItemBag(
            firstId: "a", secondId: "b", thirdId: "c");

        bag.FormIndex(1, 2);
        bag.FormIndex(0, 3);
        TestLogger.Info("FormIndex(inout Int, Int) round-trip completed without crash");
    }
}
