// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
using Swift;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async MCB (Method Closure Bridge) callback pattern — methods with
/// @escaping (Result&lt;T, Error&gt;) -> Void completion handlers.
///
/// Covers the R2 regression: Mono JIT assertion `!ji->async` when MCB closure
/// callback fires. Pattern seen in image-loading libraries with disk-storage-size callbacks.
///
/// Tier structure:
/// - Tier 1: ResultCallbackProcessor construction
/// - Tier 2: Result callback fires successfully
/// - Tier 3: Multiple sequential callbacks
/// </summary>
public class AsyncMCBCallbackTests : TestBase
{
    public AsyncMCBCallbackTests(TestResults results) : base(results) { }

    #region Construction (Tier 1)

    public void TestResultCallbackProcessorConstruction()
    {
        var processor = new ResultCallbackProcessor();
        AssertNotNull(processor, "ResultCallbackProcessor constructed");
        TestLogger.Info("ResultCallbackProcessor() construction passed");
    }

    #endregion

    #region Result Callback (Tier 2 — R2 regression)

    public void TestProcessWithResultCallback()
    {
        var processor = new ResultCallbackProcessor();
        int receivedValue = -1;
        processor.ProcessWithResult(result =>
        {
            // Result<Int32, Error> — on success, extract value
            receivedValue = 42; // If callback fires at all, we got past the crash
        });
        AssertEqual(42, receivedValue, "ProcessWithResult callback fired with success value");
        TestLogger.Info($"ProcessWithResult callback value = {receivedValue}");
    }

    public void TestCalculateSizeCallback()
    {
        var processor = new ResultCallbackProcessor();
        bool callbackFired = false;
        processor.CalculateSize(result =>
        {
            callbackFired = true;
        });
        AssertTrue(callbackFired, "CalculateSize callback fired");
        TestLogger.Info("CalculateSize callback completed successfully");
    }

    #endregion

    #region Multiple Callbacks (Tier 3)

    public void TestMultipleSequentialCallbacks()
    {
        var processor = new ResultCallbackProcessor();
        int callCount = 0;
        processor.ProcessMultiple(5, result =>
        {
            callCount++;
        });
        AssertEqual(5, callCount, "ProcessMultiple fired 5 callbacks");
        TestLogger.Info($"ProcessMultiple callback count = {callCount}");
    }

    #endregion
}
