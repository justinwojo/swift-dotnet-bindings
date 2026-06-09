// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Apple's <c>WeatherKit.Forecast&lt;Element&gt;</c> constrains <c>Element</c> by
/// <c>Decodable &amp; Encodable &amp; Equatable &amp; Sendable</c> — three non-marker PWTs
/// that carry Self requirements AND push (1 metadata + 3 PWTs) &gt; 3 register slots, flipping
/// the parent type's metadata accessor into buffer-mode ABI. The Collection-witness fallback
/// previously bailed on exactly that shape: the thin-mode metadata helper PAC-trapped, and the
/// PwtEntries gate rejected non-resolvable (descriptor-only) Self-requirement conformances.
///
/// <see cref="ForecastSeriesTests"/> covers the thin-mode-resolvable variant
/// (CollectibleItem, 0 Self requirements, 1 PWT ≤ 3 slots). This fixture
/// exercises the Apple-exact shape: three Self-requirement PWTs, private
/// storage, witness-backed projection only. If the <c>CollectionProjectionEmitter</c> rewrite
/// regresses (thin-mode helper returns, PWT gate returns, or parent metadata hand-off breaks),
/// these tests surface the regression before Apple's surface silently loses iteration again.
///
/// These tests exercise four properties the Apple surface depends on:
///   1. <c>Count</c> matches the number of elements inserted.
///   2. <c>this[int]</c> returns elements in insertion order with round-tripped
///      payloads.
///   3. <c>foreach</c> yields every element exactly once, in order.
///   4. <c>IReadOnlyList&lt;TElement&gt;</c> cast works end-to-end, including
///      LINQ.
/// </summary>
public class AppleShapedForecastTests : TestBase
{
    public AppleShapedForecastTests(TestResults results) : base(results) { }

    public void TestAppleShapedForecast_Count_MatchesInput()
    {
        // Three-element forecast — private-backed storage of exactly three
        // identifiable coins. Count must project through EndIndex - StartIndex
        // because there's no visible backing array.
        using var forecast = Functions.MakeAppleShapedForecast(
            firstId: "sunny", secondId: "cloudy", thirdId: "rain");

        AssertEqual(3, forecast.Count, "AppleShapedForecast.Count");
    }

    public void TestAppleShapedForecast_Indexer_ReturnsElementById()
    {
        // Element access must dispatch through the projected @_cdecl subscript
        // wrapper (SBW_CollProj_subscript_…). For this Apple-shaped fixture,
        // the wrapper receives the parent type metadata directly from C# (via
        // SwiftObjectHelper<AppleShapedForecast<IdentifiableCoin>>.GetTypeMetadata),
        // NOT through a thin-mode dlsym helper.
        using var forecast = Functions.MakeAppleShapedForecast(
            firstId: "mon", secondId: "tue", thirdId: "wed");

        using var first = forecast[0];
        using var second = forecast[1];
        using var third = forecast[2];

        AssertEqual("mon", first.Identifier.ToString(), "forecast[0].Identifier");
        AssertEqual("tue", second.Identifier.ToString(), "forecast[1].Identifier");
        AssertEqual("wed", third.Identifier.ToString(), "forecast[2].Identifier");
    }

    public void TestAppleShapedForecast_Foreach_YieldsInOrder()
    {
        // The ultimate consumer-facing contract — what breaks when the
        // projection drops on Apple's real Forecast<TElement>. GetEnumerator
        // iterates via the projected indexer (0..Count-1), so a regression on
        // either Count or this[int] surfaces here first.
        using var forecast = Functions.MakeAppleShapedForecast(
            firstId: "hour-0", secondId: "hour-1", thirdId: "hour-2");

        var collected = new List<string>();
        foreach (var coin in forecast)
        {
            collected.Add(coin.Identifier.ToString());
            coin.Dispose();
        }

        AssertEqual(3, collected.Count, "foreach yielded 3 elements");
        AssertEqual("hour-0", collected[0], "foreach yielded element 0");
        AssertEqual("hour-1", collected[1], "foreach yielded element 1");
        AssertEqual("hour-2", collected[2], "foreach yielded element 2");
    }

    public void TestAppleShapedForecast_IReadOnlyList_CastWorks()
    {
        // A consumer library that holds a reference as
        // IReadOnlyList<IdentifiableCoin> (e.g. a view-model binding) must be
        // able to cast cleanly — if the interface list regressed or covariance
        // broke, this cast would throw.
        using var forecast = Functions.MakeAppleShapedForecast(
            firstId: "a", secondId: "b", thirdId: "c");

        IReadOnlyList<IdentifiableCoin> view = forecast;
        AssertEqual(3, view.Count, "IReadOnlyList.Count");

        using var elementTwo = view[1];
        AssertEqual("b", elementTwo.Identifier.ToString(), "IReadOnlyList indexer");

        // LINQ drives IEnumerable<T>.GetEnumerator() — a parallel exercise of
        // the enumerator path via a consumer-shape API.
        var ids = view.Select(c => c.Identifier.ToString()).ToArray();
        AssertEqual(3, ids.Length, "LINQ Select element count");
        AssertEqual("a", ids[0], "LINQ Select[0]");
        AssertEqual("b", ids[1], "LINQ Select[1]");
        AssertEqual("c", ids[2], "LINQ Select[2]");
    }
}
