// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Async methods taking <c>Array&lt;TStruct&gt;</c> (where <c>TStruct</c> is
/// a non-frozen Swift struct, surfaced in C# as a SafeHandle-backed
/// reference type) must hold the <c>SwiftArray</c> serialization buffer
/// alive across the foreground-frame suspension — the Swift continuation
/// reads its payload after the C# foreground frame has unwound.
/// </summary>
public class AsyncNonFrozenStructArrayParamTests : TestBase
{
    public AsyncNonFrozenStructArrayParamTests(TestResults results) : base(results) { }

    public async Task TestSumPointMagnitudesAsyncEmpty()
    {
        var result = await WithTimeout(
            Functions.SumPointMagnitudesAsync(Array.Empty<NonFrozenPoint>()),
            DefaultAsyncTimeout);
        AssertEqual(0.0, result, "Empty array async sum");
        TestLogger.Info($"SumPointMagnitudesAsync(empty) = {result}");
    }

    public async Task TestSumPointMagnitudesAsyncPopulated()
    {
        var points = new[]
        {
            new NonFrozenPoint(3.0, 4.0),  // magnitude 5
            new NonFrozenPoint(0.0, 0.0),  // magnitude 0
            new NonFrozenPoint(6.0, 8.0),  // magnitude 10
        };
        var result = await WithTimeout(
            Functions.SumPointMagnitudesAsync(points),
            DefaultAsyncTimeout);
        AssertEqual(15.0, result, "Sum of magnitudes 5 + 0 + 10");
        TestLogger.Info($"SumPointMagnitudesAsync(3 points) = {result}");
    }

    public async Task TestScalePointsAsyncRoundTrip()
    {
        var points = new[]
        {
            new NonFrozenPoint(1.0, 2.0),
            new NonFrozenPoint(3.0, 4.0),
            new NonFrozenPoint(5.0, 6.0),
        };
        var scaled = await WithTimeout(
            Functions.ScalePointsAsync(points, 2.5),
            DefaultAsyncTimeout);
        AssertEqual(3, scaled.Count, "Scaled array length");
        AssertEqual(2.5, scaled[0].X, "scaled[0].x");
        AssertEqual(5.0, scaled[0].Y, "scaled[0].y");
        AssertEqual(7.5, scaled[1].X, "scaled[1].x");
        AssertEqual(10.0, scaled[1].Y, "scaled[1].y");
        AssertEqual(12.5, scaled[2].X, "scaled[2].x");
        AssertEqual(15.0, scaled[2].Y, "scaled[2].y");
        TestLogger.Info($"ScalePointsAsync round-trip [{scaled.Count}]");
    }

    /// <summary>
    /// Larger payload — increases the odds of catching the lifetime bug if
    /// the buffer is disposed prematurely (more bytes touched by the Swift
    /// continuation = larger window for use-after-free to surface as a
    /// fault rather than a silent miscompare).
    /// </summary>
    public async Task TestSumPointMagnitudesAsyncManyPoints()
    {
        var points = new NonFrozenPoint[64];
        var expected = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new NonFrozenPoint(i, i);
            expected += new NonFrozenPoint(i, i).GetDistanceFromOrigin();
        }
        var result = await WithTimeout(
            Functions.SumPointMagnitudesAsync(points),
            DefaultAsyncTimeout);
        AssertTrue(Math.Abs(result - expected) < 0.001, $"Sum {result} ≈ {expected}");
        TestLogger.Info($"SumPointMagnitudesAsync(64 points) = {result}");
    }
}
