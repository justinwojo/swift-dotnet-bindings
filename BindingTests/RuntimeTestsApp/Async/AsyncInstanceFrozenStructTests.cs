// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async INSTANCE methods with frozen blittable struct parameters.
/// Validates the heap allocation fix covers instance methods, not just free/static
/// functions (already covered by AsyncFrozenStructParamTests). Before the fix,
/// instance methods with frozen struct params could crash (stack buffer invalid
/// across await boundary).
/// </summary>
public class AsyncInstanceFrozenStructTests : TestBase
{
    public AsyncInstanceFrozenStructTests(TestResults results) : base(results) { }

    public async Task TestProcessPointInstanceMethod()
    {
        var processor = new PointProcessor("proc");
        var point = new FrozenPoint(3.0, 4.0);
        var result = await WithTimeout(
            processor.ProcessPointAsync(point),
            DefaultAsyncTimeout);
        AssertEqual("proc: (3.0, 4.0)", result, "Instance processPoint result");
        TestLogger.Info($"PointProcessor.ProcessPointAsync((3.0, 4.0)) = \"{result}\"");
    }

    public async Task TestScalePointInstanceMethod()
    {
        var processor = new PointProcessor("scaler");
        var point = new FrozenPoint(2.0, 5.0);
        var result = await WithTimeout(
            processor.ScalePointAsync(point, 3.0),
            DefaultAsyncTimeout);
        AssertEqual(6.0, result.X, "Scaled X");
        AssertEqual(15.0, result.Y, "Scaled Y");
        TestLogger.Info($"PointProcessor.ScalePointAsync((2.0, 5.0), 3.0) = ({result.X}, {result.Y})");
    }

    public async Task TestAddPointsInstanceMethod()
    {
        var processor = new PointProcessor("adder");
        var a = new FrozenPoint(1.0, 2.0);
        var b = new FrozenPoint(3.0, 4.0);
        var result = await WithTimeout(
            processor.AddPointsAsync(a, b),
            DefaultAsyncTimeout);
        AssertEqual(4.0, result.X, "Added X");
        AssertEqual(6.0, result.Y, "Added Y");
        TestLogger.Info($"PointProcessor.AddPointsAsync result = ({result.X}, {result.Y})");
    }

    public async Task TestValidatePointInstanceMethodValid()
    {
        var processor = new PointProcessor("validator");
        var point = new FrozenPoint(1.0, 2.0);
        var result = await WithTimeout(
            processor.ValidatePointAsync(point),
            DefaultAsyncTimeout);
        AssertTrue(result, "Valid point passes validation");
        TestLogger.Info($"PointProcessor.ValidatePointAsync((1.0, 2.0)) = {result}");
    }

    public async Task TestValidatePointInstanceMethodInvalid()
    {
        var processor = new PointProcessor("validator");
        var point = new FrozenPoint(-1.0, 2.0);
        var result = await WithTimeout(
            processor.ValidatePointAsync(point),
            DefaultAsyncTimeout);
        AssertFalse(result, "Negative X fails validation");
        TestLogger.Info($"PointProcessor.ValidatePointAsync((-1.0, 2.0)) = {result}");
    }
}
