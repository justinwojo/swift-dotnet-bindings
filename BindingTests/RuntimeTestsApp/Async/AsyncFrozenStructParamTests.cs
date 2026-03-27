// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async methods with frozen blittable struct parameters.
/// Validates the heap allocation fix: frozen blittable struct params in async methods
/// must use NativeMemory.Alloc instead of stackalloc, because the stack buffer is
/// invalidated across the await boundary.
/// Before the fix, these tests could crash (stack buffer invalid by callback time).
/// </summary>
public class AsyncFrozenStructParamTests : TestBase
{
    public AsyncFrozenStructParamTests(TestResults results) : base(results) { }

    public async Task TestAsyncProcessFrozenPoint()
    {
        var point = new FrozenPoint(3.0, 4.0);
        var result = await WithTimeout(
            Functions.ProcessFrozenPointAsync(point),
            DefaultAsyncTimeout);
        AssertEqual("(3.0, 4.0)", result, "AsyncProcessFrozenPoint result");
        TestLogger.Info($"AsyncProcessFrozenPoint((3.0, 4.0)) = {result}");
    }

    public async Task TestAsyncScaleFrozenPoint()
    {
        var point = new FrozenPoint(2.0, 3.0);
        var result = await WithTimeout(
            Functions.ScaleFrozenPointAsync(point, 2.5),
            DefaultAsyncTimeout);
        AssertEqual(5.0, result.X, "Scaled X");
        AssertEqual(7.5, result.Y, "Scaled Y");
        TestLogger.Info($"AsyncScaleFrozenPoint((2.0, 3.0), 2.5) = ({result.X}, {result.Y})");
    }

    public async Task TestAsyncCombineFrozenPoints()
    {
        var a = new FrozenPoint(1.0, 2.0);
        var b = new FrozenPoint(3.0, 4.0);
        var result = await WithTimeout(
            Functions.CombineFrozenPointsAsync(a, b),
            DefaultAsyncTimeout);
        AssertEqual(4.0, result.X, "Combined X");
        AssertEqual(6.0, result.Y, "Combined Y");
        TestLogger.Info($"AsyncCombineFrozenPoints result = ({result.X}, {result.Y})");
    }

    public async Task TestAsyncValidateFrozenPointValid()
    {
        var point = new FrozenPoint(1.0, 2.0);
        var result = await WithTimeout(
            Functions.ValidateFrozenPointAsync(point),
            DefaultAsyncTimeout);
        AssertTrue(result, "Valid point should pass validation");
        TestLogger.Info($"AsyncValidateFrozenPoint((1.0, 2.0)) = {result}");
    }

    public async Task TestAsyncValidateFrozenPointInvalid()
    {
        var point = new FrozenPoint(-1.0, 2.0);
        var result = await WithTimeout(
            Functions.ValidateFrozenPointAsync(point),
            DefaultAsyncTimeout);
        AssertFalse(result, "Negative X should fail validation");
        TestLogger.Info($"AsyncValidateFrozenPoint((-1.0, 2.0)) = {result}");
    }
}
