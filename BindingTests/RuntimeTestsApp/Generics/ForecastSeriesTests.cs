// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Apple's <c>WeatherKit.Forecast&lt;Element&gt;</c> conforms to Swift's <c>Collection</c>
/// but has PRIVATE storage — only <c>startIndex</c>, <c>endIndex</c>,
/// <c>subscript(Int) -&gt; Element</c>, and <c>index(after:)</c> are public. Before the fix in
/// <c>CollectionProjectionEmitter</c>, the projection only fired when a public
/// <c>Swift.Array&lt;Element&gt;</c> backing property was emittable, so the
/// projection was silently dropped on <c>Forecast&lt;Element&gt;</c> and
/// consumers could not iterate a forecast.
///
/// The fix adds a witness-dispatch fallback: the projection emits
/// <c>Count</c> via <c>EndIndex - StartIndex</c>, <c>this[int]</c> via a
/// freshly-minted <c>@_cdecl</c> subscript wrapper (generic static dispatch),
/// and <c>GetEnumerator()</c> iterates through the projected indexer. This
/// fixture exercises exactly that shape — private backing, no public array
/// property, only the Collection requirements visible — so regressions surface
/// here first.
///
/// These tests exercise three properties the Apple surface depends on:
///   1. <c>Count</c> matches the number of elements inserted.
///   2. <c>this[int]</c> returns elements in insertion order with round-tripped
///      payloads, not sentinels or empty values.
///   3. <c>foreach</c> yields every element exactly once, in order — the
///      ultimate consumer-facing contract.
/// </summary>
public class ForecastSeriesTests : TestBase
{
    public ForecastSeriesTests(TestResults results) : base(results) { }

    public void TestForecastSeries_Count_MatchesInput()
    {
        // Three-element forecast — private-backed storage of exactly three coins.
        // Count must project through EndIndex - StartIndex because there's no
        // visible backing array to delegate to.
        using var series = Functions.MakeForecastSeries(
            firstId: "sunny", secondId: "cloudy", thirdId: "rain");

        AssertEqual(3, series.Count, "ForecastSeries.Count");
    }

    public void TestForecastSeries_Indexer_ReturnsElementById()
    {
        // Element access must dispatch through the projected @_cdecl subscript
        // wrapper (SBW_CollProj_subscript_…) → generic static dispatch → the
        // struct's subscript(Int) -> Element witness. Each read round-trips the
        // CollectibleCoin payload so we can verify elements arrive in order,
        // not scrambled by the metadata / PWT plumbing.
        using var series = Functions.MakeForecastSeries(
            firstId: "mon", secondId: "tue", thirdId: "wed");

        using var first = series[0];
        using var second = series[1];
        using var third = series[2];

        AssertEqual("mon", first.CollectibleId, "series[0].CollectibleId");
        AssertEqual("tue", second.CollectibleId, "series[1].CollectibleId");
        AssertEqual("wed", third.CollectibleId, "series[2].CollectibleId");
    }

    public void TestForecastSeries_Foreach_YieldsInOrder()
    {
        // The ultimate consumer-facing contract: `foreach (var hour in forecast)`
        // must compile and yield every element exactly once in index order.
        // GetEnumerator iterates via the projected indexer (0..Count-1), so a
        // regression anywhere on the Count path or the this[int] path shows up
        // here as a mismatched ID list or an off-by-one error.
        using var series = Functions.MakeForecastSeries(
            firstId: "hour-0", secondId: "hour-1", thirdId: "hour-2");

        var collected = new List<string>();
        foreach (var coin in series)
        {
            collected.Add(coin.CollectibleId);
            coin.Dispose();
        }

        AssertEqual(3, collected.Count, "foreach yielded 3 elements");
        AssertEqual("hour-0", collected[0], "foreach yielded element 0");
        AssertEqual("hour-1", collected[1], "foreach yielded element 1");
        AssertEqual("hour-2", collected[2], "foreach yielded element 2");
    }

    public void TestForecastSeries_IReadOnlyList_CastWorks()
    {
        // The projected interface is IReadOnlyList<TElement>. A consumer library
        // that holds a reference as IReadOnlyList<CollectibleCoin> (e.g. a view
        // model binding) must be able to cast cleanly — if the interface list
        // regressed or covariance broke, this cast would throw.
        using var series = Functions.MakeForecastSeries(
            firstId: "a", secondId: "b", thirdId: "c");

        IReadOnlyList<CollectibleCoin> view = series;
        AssertEqual(3, view.Count, "IReadOnlyList.Count");

        using var elementTwo = view[1];
        AssertEqual("b", elementTwo.CollectibleId, "IReadOnlyList indexer");

        // LINQ runs off IEnumerable<T>.GetEnumerator() — a parallel exercise of
        // the enumerator path via a consumer-shape API.
        var ids = view.Select(c => c.CollectibleId).ToArray();
        AssertEqual(3, ids.Length, "LINQ Select element count");
        AssertEqual("a", ids[0], "LINQ Select[0]");
        AssertEqual("b", ids[1], "LINQ Select[1]");
        AssertEqual("c", ids[2], "LINQ Select[2]");
    }

    public void TestForecastSeries_EmptyCollection_IndexZeroThrows()
    {
        // The shape a consumer reaches after a search that matched nothing.
        // Swift's Collection subscript is a precondition: evaluating obj[0] on an
        // empty collection traps the whole process. Read through the interface
        // because that is how a consumer holds it, and an IReadOnlyList<T> is
        // expected to raise a catchable ArgumentOutOfRangeException here.
        using var empty = Functions.MakeEmptyForecastSeries();
        IReadOnlyList<CollectibleCoin> view = empty;

        AssertEqual(0, view.Count, "empty ForecastSeries.Count");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[0]; }, "empty series[0] throws");
    }

    public void TestForecastSeries_OutOfRangeIndices_Throw()
    {
        // Negative and Count are the two other ordinary bounds errors. Both are
        // rejected before the native subscript is evaluated, so neither reaches
        // Swift's trap. The successful read afterwards is the positive control
        // that the bounds shim did not break element access.
        using var series = Functions.MakeForecastSeries(
            firstId: "a", secondId: "b", thirdId: "c");
        IReadOnlyList<CollectibleCoin> view = series;

        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[-1]; }, "series[-1] throws");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[view.Count]; }, "series[Count] throws");

        using var last = view[view.Count - 1];
        AssertEqual("c", last.CollectibleId, "series[Count - 1] still reads");
    }
}
