// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Sister fixture to <see cref="AsyncNonFrozenStructArrayParamTests"/> and
/// <see cref="Marshalling.SetParameterDefaultTests"/> SetMembershipCountAsync
/// — locks down the third <see cref="System.IDisposable"/> serialization
/// container (<c>SwiftDictionary&lt;K,V&gt;</c>) on the async hand-off path.
/// Without the deferred-dispose hand-off the SwiftDictionary's
/// <c>using var</c> would dispose the buffer the moment the foreground
/// wrapper returns <c>tcs.Task</c>.
/// </summary>
public class AsyncNonFrozenStructDictionaryParamTests : TestBase
{
    public AsyncNonFrozenStructDictionaryParamTests(TestResults results) : base(results) { }

    public async Task TestCountPointsWithMagnitudeAsyncEmpty()
    {
        var result = await WithTimeout(
            Functions.CountPointsWithMagnitudeAsync(
                new Dictionary<string, NonFrozenPoint>(),
                threshold:1.0),
            DefaultAsyncTimeout);
        AssertEqual((nint)0, result, "Empty dictionary async count");
        TestLogger.Info($"CountPointsWithMagnitudeAsync(empty) = {result}");
    }

    public async Task TestCountPointsWithMagnitudeAsyncPopulated()
    {
        var points = new Dictionary<string, NonFrozenPoint>
        {
            ["origin"] = new NonFrozenPoint(0.0, 0.0),    // magnitude 0
            ["near"] = new NonFrozenPoint(3.0, 4.0),      // magnitude 5
            ["far"] = new NonFrozenPoint(6.0, 8.0),       // magnitude 10
            ["very-far"] = new NonFrozenPoint(30.0, 40.0), // magnitude 50
        };
        var result = await WithTimeout(
            Functions.CountPointsWithMagnitudeAsync(points, threshold:5.0),
            DefaultAsyncTimeout);
        AssertEqual((nint)3, result, "3 entries have magnitude ≥ 5");
        TestLogger.Info($"CountPointsWithMagnitudeAsync(4 entries, threshold=5) = {result}");
    }

    /// <summary>
    /// Larger payload — same rationale as the sister Array fixture's
    /// many-points test. More bytes = larger use-after-free window if
    /// the buffer is disposed before the Swift continuation reads it.
    /// </summary>
    public async Task TestCountPointsWithMagnitudeAsyncManyEntries()
    {
        var points = new Dictionary<string, NonFrozenPoint>();
        for (int i = 0; i < 64; i++)
        {
            points[$"k{i}"] = new NonFrozenPoint(i, i);
        }
        // distanceFromOrigin = sqrt(2)*i; want all entries with i ≥ 10 (magnitude ≥ 14.14)
        var result = await WithTimeout(
            Functions.CountPointsWithMagnitudeAsync(points, threshold:14.14),
            DefaultAsyncTimeout);
        AssertEqual((nint)54, result, "Entries 10..63 inclusive ≥ threshold (54 total)");
        TestLogger.Info($"CountPointsWithMagnitudeAsync(64 entries) = {result}");
    }
}
