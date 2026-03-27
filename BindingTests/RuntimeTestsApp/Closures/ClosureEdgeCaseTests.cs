// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests for closure edge cases: throwing closures, completion handler patterns,
/// and closure return type variations (frozen struct, enum, non-frozen struct, class).
/// </summary>
public class ClosureEdgeCaseTests : TestBase
{
    public ClosureEdgeCaseTests(TestResults results) : base(results) { }

    #region Completion Handler Callbacks

    public void TestCompletionServiceFetchData()
    {
        var service = new CompletionService();
        var called = false;
        service.FetchData(() => { called = true; });
        AssertTrue(called, "FetchData completion handler called");
        TestLogger.Info("CompletionService.FetchData callback passed");
    }

    public void TestCompletionServiceFetchValue()
    {
        var service = new CompletionService();
        var capturedValue = 0;
        service.FetchValue(v => { capturedValue = v; });
        AssertEqual(42, capturedValue, "FetchValue completion returns 42");
        TestLogger.Info($"CompletionService.FetchValue callback = {capturedValue}");
    }

    public void TestCompletionServiceFetchWithFlag()
    {
        var service = new CompletionService();
        var capturedValue = 0;
        var capturedFlag = false;
        service.FetchWithFlag((v, f) => { capturedValue = v; capturedFlag = f; });
        AssertEqual(100, capturedValue, "FetchWithFlag value = 100");
        AssertTrue(capturedFlag, "FetchWithFlag flag = true");
        TestLogger.Info($"CompletionService.FetchWithFlag = ({capturedValue}, {capturedFlag})");
    }

    public void TestPerformActionCallback()
    {
        var called = false;
        TestLibFunctions.PerformAction(() => { called = true; });
        AssertTrue(called, "PerformAction completion handler called");
        TestLogger.Info("PerformAction callback passed");
    }

    public void TestComputeValueCallback()
    {
        var capturedValue = 0;
        TestLibFunctions.ComputeValue(v => { capturedValue = v; });
        AssertEqual(99, capturedValue, "ComputeValue completion returns 99");
        TestLogger.Info($"ComputeValue callback = {capturedValue}");
    }

    #endregion

    #region Completion Handler Async Overloads

    public async Task TestFetchDataAsync()
    {
        var service = new CompletionService();
        await WithTimeout(service.FetchDataAsync(), DefaultAsyncTimeout);
        TestLogger.Info("CompletionService.FetchDataAsync completed");
    }

    public async Task TestFetchValueAsync()
    {
        var service = new CompletionService();
        var result = await WithTimeout(service.FetchValueAsync(), DefaultAsyncTimeout);
        AssertEqual(42, result, "FetchValueAsync returns 42");
        TestLogger.Info($"CompletionService.FetchValueAsync = {result}");
    }

    public async Task TestPerformActionAsync()
    {
        // Async overloads for free functions are instance methods on Functions class
        var functions = new SwiftBindingsTestLib.Functions();
        await WithTimeout(functions.PerformActionAsync(), DefaultAsyncTimeout);
        TestLogger.Info("PerformActionAsync completed");
    }

    public async Task TestComputeValueAsync()
    {
        // Async overloads for free functions are instance methods on Functions class
        var functions = new SwiftBindingsTestLib.Functions();
        var result = await WithTimeout(functions.ComputeValueAsync(), DefaultAsyncTimeout);
        AssertEqual(99, result, "ComputeValueAsync returns 99");
        TestLogger.Info($"ComputeValueAsync = {result}");
    }

    #endregion

    #region Closure Return Types — Direct Return

    [Skip("Generator bug: @convention(c) cannot return Swift structs — wrapper stripped at compile time")]
    public void TestClosureReturningFrozenPoint()
    {
        // () -> FrozenPoint — frozen struct returned directly by value
        var result = TestLibFunctions.CallWithFrozenPointReturn(() => new FrozenPoint(x: 3.0, y: 4.0));
        AssertEqual(3.0, result.X, "FrozenPoint.X = 3.0");
        AssertEqual(4.0, result.Y, "FrozenPoint.Y = 4.0");
        TestLogger.Info($"CallWithFrozenPointReturn = ({result.X}, {result.Y})");
    }

    [Skip("Generator bug: unsafeBitCast between Int32 and Color crashes at runtime with size mismatch")]
    public void TestClosureReturningEnum()
    {
        // () -> Color — simple enum returned as underlying integer
        var result = TestLibFunctions.CallWithEnumReturn(() => Color.Blue);
        AssertEqual(Color.Blue, result, "CallWithEnumReturn returns Blue");
        TestLogger.Info($"CallWithEnumReturn = {result}");
    }

    [Skip("Generator bug: @convention(c) cannot return Swift structs — wrapper stripped at compile time")]
    public void TestClosureReturningFrozenPointWithParam()
    {
        // (Double) -> FrozenPoint — direct return with parameter
        var result = TestLibFunctions.CallWithFrozenPointTransform(5.0, v => new FrozenPoint(x: v, y: v * 2));
        AssertEqual(5.0, result.X, "FrozenPointTransform.X = 5.0");
        AssertEqual(10.0, result.Y, "FrozenPointTransform.Y = 10.0");
        TestLogger.Info($"CallWithFrozenPointTransform = ({result.X}, {result.Y})");
    }

    public void TestClosureReturningBool()
    {
        // (Int32) -> Bool — special byte↔bool conversion
        var result = TestLibFunctions.CallWithBoolReturn(x => x > 40);
        AssertTrue(result, "CallWithBoolReturn(42 > 40) = true");
        TestLogger.Info($"CallWithBoolReturn = {result}");
    }

    #endregion

    #region Closure Return Types — Indirect Return

    public void TestClosureReturningNonFrozenPoint()
    {
        // () -> NonFrozenPoint — non-frozen struct uses indirect return marshalling
        var result = TestLibFunctions.CallWithNonFrozenReturn(() => new NonFrozenPoint(x: 7.0, y: 8.0));
        AssertEqual(7.0, result.X, "NonFrozenPoint.X = 7.0");
        AssertEqual(8.0, result.Y, "NonFrozenPoint.Y = 8.0");
        TestLogger.Info($"CallWithNonFrozenReturn = ({result.X}, {result.Y})");
    }

    public void TestClosureReturningClass()
    {
        // () -> FinalCounter — class uses indirect return with memory management
        var result = TestLibFunctions.CallWithClassReturn(() => new FinalCounter(count: 99));
        AssertEqual(99, result.Count, "FinalCounter.Count = 99");
        TestLogger.Info($"CallWithClassReturn count = {result.Count}");
    }

    #endregion

    #region Throwing Closures — Success Paths

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion")]
    public void TestThrowingClosureSuccess()
    {
        // () throws -> Int32 — use raw SwiftResult overload
        var result = TestLibFunctions.CallThrowingClosure(
            () => Swift.SwiftResult<int, SwiftError>.FromSuccess(42));
        AssertEqual(42, result, "CallThrowingClosure success returns 42");
        TestLogger.Info($"CallThrowingClosure success = {result}");
    }

    [Skip("Generator bug: throwing closure wrapper with String return assigns UnsafeMutableRawPointer to String — wrapper stripped")]
    public void TestThrowingWithParamSuccess()
    {
        // (Int32) throws -> String — use raw SwiftResult overload
        var result = TestLibFunctions.CallThrowingWithParam(
            x => Swift.SwiftResult<string, SwiftError>.FromSuccess($"value={x}"));
        AssertEqual("value=42", result, "CallThrowingWithParam success returns value=42");
        TestLogger.Info($"CallThrowingWithParam success = {result}");
    }

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion")]
    public void TestThrowingVoidSuccess()
    {
        // () throws -> Void — use raw SwiftResult overload
        var result = TestLibFunctions.CallThrowingVoid(
            () => Swift.SwiftResult<Swift.SwiftVoid, SwiftError>.FromSuccess(default));
        AssertTrue(result, "CallThrowingVoid success returns true");
        TestLogger.Info($"CallThrowingVoid success = {result}");
    }

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — callback with SwiftError* triggers !ji->async assertion")]
    public void TestThrowingBoolSuccess()
    {
        // (Int32) throws -> Bool — use raw SwiftResult overload
        var result = TestLibFunctions.CallThrowingBool(
            x => Swift.SwiftResult<bool, SwiftError>.FromSuccess(x > 5));
        AssertTrue(result, "CallThrowingBool(10 > 5) returns true");
        TestLogger.Info($"CallThrowingBool success = {result}");
    }

    #endregion
}
