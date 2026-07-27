// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Async member generic over <c>Collection</c> with <c>Element == String</c>, returning an array of
/// resilient structs: <c>IEnumerable&lt;string&gt;</c> → <c>SwiftArray&lt;SwiftString&gt;</c> →
/// async <c>@_cdecl</c> wrapper reading <c>Array&lt;String&gt;</c> → <c>[resilient struct]</c> back
/// to C#.
/// <para>
/// This is the shape behind StoreKit's <c>Product.products(for:)</c>. A field report of
/// "0 results returned, no error" could only originate in the binding if this round-trip dropped or
/// emptied the input array, or returned an empty result for a non-empty input.
/// </para>
/// </summary>
public class AsyncGenericCollectionStructArrayReturnTests : TestBase
{
    public AsyncGenericCollectionStructArrayReturnTests(TestResults results) : base(results) { }

    // The >15 UTF-8-byte identifiers force HEAP-allocated Swift strings (not small-string-optimized),
    // exactly like real reverse-DNS identifiers — a small-string-only test would miss the heap-string
    // ownership path inside a returned resilient struct.
    private static readonly string[] Identifiers =
    {
        "com.example.app.premium_subscription_yearly",
        "com.example.app.coins_100",
        "com.example.app.remove_ads",
    };

    /// <summary>Generic <c>records(for:)</c> round-trips count and every string field.</summary>
    public async Task TestRecordsForRoundTrip()
    {
        var records = await WithTimeout(
            NonFrozenIdentifiedRecord.RecordsAsync(Identifiers),
            DefaultAsyncTimeout);

        AssertEqual(3, records.Count, "record count must equal input element count");
        for (int i = 0; i < Identifiers.Length; i++)
        {
            AssertEqual(Identifiers[i], records[i].Id.ToString(), $"records[{i}].Id round-trip");
            AssertEqual("name:" + Identifiers[i], records[i].DisplayName.ToString(), $"records[{i}].DisplayName");
        }
        TestLogger.Info($"NonFrozenIdentifiedRecord.RecordsAsync([{Identifiers.Length}]) -> {records.Count} records, ids intact");
    }

    /// <summary>Single element resolves to exactly one record.</summary>
    public async Task TestRecordsForSingle()
    {
        var records = await WithTimeout(
            NonFrozenIdentifiedRecord.RecordsAsync(new[] { "com.example.app.single_long_identifier" }),
            DefaultAsyncTimeout);
        AssertEqual(1, records.Count, "single-element record count");
        AssertEqual("com.example.app.single_long_identifier", records[0].Id.ToString(), "single id round-trip");
        TestLogger.Info($"NonFrozenIdentifiedRecord.RecordsAsync(1 id) -> {records.Count}");
    }

    /// <summary>
    /// Empty input must yield empty output cleanly (no crash) — the ONLY case where a correct
    /// binding legitimately returns 0. Distinguishes "binding emptied the array" from "caller
    /// passed nothing".
    /// </summary>
    public async Task TestRecordsForEmpty()
    {
        var records = await WithTimeout(
            NonFrozenIdentifiedRecord.RecordsAsync(Array.Empty<string>()),
            DefaultAsyncTimeout);
        AssertEqual(0, records.Count, "empty input -> empty output");
        TestLogger.Info("NonFrozenIdentifiedRecord.RecordsAsync(empty) -> 0 (clean)");
    }

    /// <summary>Concrete <c>[String]</c> param — isolates SwiftArray&lt;String&gt; serialization from generic dispatch.</summary>
    public async Task TestRecordsConcreteRoundTrip()
    {
        var records = await WithTimeout(
            Functions.FetchIdentifiedRecordsConcreteAsync(Identifiers),
            DefaultAsyncTimeout);
        AssertEqual(3, records.Count, "concrete record count");
        for (int i = 0; i < Identifiers.Length; i++)
            AssertEqual(Identifiers[i], records[i].Id.ToString(), $"concrete records[{i}].Id");
        TestLogger.Info($"Functions.FetchIdentifiedRecordsConcreteAsync -> {records.Count} records");
    }
}
