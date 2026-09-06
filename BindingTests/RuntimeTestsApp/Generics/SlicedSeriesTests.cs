// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>SlicedSeries&lt;Element&gt;</c> is a plain <c>Collection</c> — no
/// <c>RandomAccessCollection</c>, no <c>BidirectionalCollection</c> — backed by an
/// <c>ArraySlice</c> that starts at native index 2. It is the sibling of
/// <c>OffsetWindowTests</c>: same zero-based <c>IReadOnlyList&lt;T&gt;</c> contract, but the
/// collection publishes only forward traversal, and its nonzero start comes from the slice
/// itself rather than a base the type carries.
///
/// Slices are how a nonzero <c>startIndex</c> actually reaches a consumer in practice —
/// <c>dropFirst</c>, <c>prefix</c>, <c>suffix</c> and friends all return one.
/// </summary>
public class SlicedSeriesTests : TestBase
{
    public SlicedSeriesTests(TestResults results) : base(results) { }

    public void TestSlicedSeries_FixtureReallyStartsAtNonZeroIndex()
    {
        // Positive control — the slice drops two elements, so its native index space is 2..5.
        using var series = Functions.MakeSlicedSeries(
            firstId: "a", secondId: "b", thirdId: "c");

        AssertEqual(2, series.SliceStart, "SlicedSeries native start index");
    }

    public void TestSlicedSeries_ZeroBasedAccessAndOrder()
    {
        // Managed position 0 is the slice's first element ("a"), never the dropped element
        // that lives at native index 0. Count is the slice's length, not the backing array's.
        using var series = Functions.MakeSlicedSeries(
            firstId: "a", secondId: "b", thirdId: "c");
        IReadOnlyList<CollectibleCoin> view = series;

        AssertEqual(3, view.Count, "SlicedSeries.Count");

        using var first = view[0];
        using var second = view[1];
        using var third = view[view.Count - 1];

        AssertEqual("a", first.CollectibleId, "view[0]");
        AssertEqual("b", second.CollectibleId, "view[1]");
        AssertEqual("c", third.CollectibleId, "view[Count - 1]");
    }

    public void TestSlicedSeries_ForeachYieldsSliceContentsOnly()
    {
        // The dropped elements must not appear: the enumerator walks the collection's own
        // count from managed position 0, and each step translates onto the slice's indices.
        using var series = Functions.MakeSlicedSeries(
            firstId: "s-0", secondId: "s-1", thirdId: "s-2");

        var collected = new List<string>();
        foreach (var coin in series)
        {
            collected.Add(coin.CollectibleId);
            coin.Dispose();
        }

        AssertEqual(3, collected.Count, "foreach yielded 3 elements");
        AssertEqual("s-0", collected[0], "foreach element 0");
        AssertEqual("s-1", collected[1], "foreach element 1");
        AssertEqual("s-2", collected[2], "foreach element 2");
    }

    public void TestSlicedSeries_OutOfRangeOffsetsThrow()
    {
        // A forward-only Collection reaches the requested offset by walking from startIndex,
        // so an offset past the end must be rejected up front rather than walked off the end.
        using var series = Functions.MakeSlicedSeries(
            firstId: "a", secondId: "b", thirdId: "c");
        IReadOnlyList<CollectibleCoin> view = series;

        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[-1]; }, "view[-1] throws");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[view.Count]; }, "view[Count] throws");

        using var stillReadable = view[view.Count - 1];
        AssertEqual("c", stillReadable.CollectibleId, "view[Count - 1] still reads");
    }

    public void TestSlicedSeries_Empty_CountZeroAndNoElements()
    {
        // An empty slice taken past the end of a non-empty array: startIndex == endIndex == 2,
        // count == 0. The shape a consumer reaches after filtering everything out.
        using var empty = Functions.MakeEmptySlicedSeries();
        IReadOnlyList<CollectibleCoin> view = empty;

        AssertEqual(0, view.Count, "empty SlicedSeries.Count");
        AssertEqual(2, empty.SliceStart, "empty SlicedSeries native start index");

        var enumerated = 0;
        foreach (var coin in empty)
        {
            enumerated++;
            coin.Dispose();
        }
        AssertEqual(0, enumerated, "empty SlicedSeries yields no elements");

        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[0]; }, "empty view[0] throws");
    }
}
