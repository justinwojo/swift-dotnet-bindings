// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for AsyncWorker (basic async methods) and AsyncThrowingWorker.
/// Class name "AsyncMethodTests" sorts alphabetically before "EnumMarshallingTests"
/// (the Mono JIT crash point that kills the process).
///
/// Tier 2: Constructors take string params (SwiftString through CallConvSwift).
/// Async P/Invokes use Cdecl callbacks (not CallConvSwift), avoiding the Mono JIT assertion.
/// </summary>
/// Tier 3: Async P/Invokes have two blocking issues:
/// 1. EntryPointNotFoundException — DllImport("SwiftBindingsTestLib") but _async entry points
///    are defined via @_silgen_name in the SwiftBindings wrapper library
/// 2. InvalidProgramException — throwing async methods pass non-blittable function pointers
///    through CallConvSwift (Mono limitation)
[TestTier(TestTier.Tier3)]
public class AsyncMethodTests : TestBase
{
    public AsyncMethodTests(TestResults results) : base(results) { }

    #region AsyncWorker - Basic Async Methods

    public async Task TestAsyncVoidMethod()
    {
        var worker = new AsyncWorker("test-worker");
        // Async void method — should complete without error
        await WithTimeout(worker.AsyncVoidMethodAsync(), DefaultAsyncTimeout);
        TestLogger.Info("AsyncWorker.AsyncVoidMethod() completed");
    }

    public async Task TestAsyncReturnMethod()
    {
        var worker = new AsyncWorker("test-worker");
        var result = await WithTimeout(worker.AsyncReturnMethodAsync(), DefaultAsyncTimeout);
        AssertEqual(42, result, "AsyncReturnMethod should return 42");
        TestLogger.Info($"AsyncWorker.AsyncReturnMethod() = {result}");
    }

    public async Task TestAsyncStringMethod()
    {
        var worker = new AsyncWorker("Bob");
        var result = await WithTimeout(worker.AsyncStringMethodAsync(), DefaultAsyncTimeout);
        AssertEqual("Hello from Bob", result, "AsyncStringMethod should return 'Hello from Bob'");
        TestLogger.Info($"AsyncWorker.AsyncStringMethod() = {result}");
    }

    public async Task TestAsyncStaticVoid()
    {
        await WithTimeout(AsyncWorker.AsyncStaticVoidAsync(), DefaultAsyncTimeout);
        TestLogger.Info("AsyncWorker.AsyncStaticVoidAsync() completed");
    }

    public async Task TestAsyncStaticReturn()
    {
        var result = await WithTimeout(AsyncWorker.AsyncStaticReturnAsync(), DefaultAsyncTimeout);
        AssertEqual(99, result, "AsyncStaticReturn should return 99");
        TestLogger.Info($"AsyncWorker.AsyncStaticReturnAsync() = {result}");
    }

    public async Task TestAsyncAdd()
    {
        var worker = new AsyncWorker("adder");
        var result = await WithTimeout(worker.AsyncAddAsync(17, 25), DefaultAsyncTimeout);
        AssertEqual(42, result, "AsyncAdd(17, 25) should return 42");
        TestLogger.Info($"AsyncWorker.AsyncAdd(17, 25) = {result}");
    }

    public async Task TestAsyncAddZero()
    {
        var worker = new AsyncWorker("adder");
        var result = await WithTimeout(worker.AsyncAddAsync(0, 0), DefaultAsyncTimeout);
        AssertEqual(0, result, "AsyncAdd(0, 0) should return 0");
        TestLogger.Info($"AsyncWorker.AsyncAdd(0, 0) = {result}");
    }

    #endregion

    #region AsyncThrowingWorker - Async Throwing Methods

    public async Task TestAsyncThrowingMethodSuccess()
    {
        var worker = new AsyncThrowingWorker("thrower");
        var result = await WithTimeout(worker.AsyncThrowingMethodAsync(false), DefaultAsyncTimeout);
        AssertEqual(42, result, "AsyncThrowingMethod(false) should return 42");
        TestLogger.Info($"AsyncThrowingWorker.AsyncThrowingMethod(false) = {result}");
    }

    public async Task TestAsyncThrowingMethodThrows()
    {
        var worker = new AsyncThrowingWorker("thrower");
        try
        {
            await WithTimeout(worker.AsyncThrowingMethodAsync(true), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"AsyncThrowingMethod(true) threw SwiftException: {ex.Message}");
            // Expected — Swift threw AsyncError.requestedThrow
        }
    }

    public async Task TestAsyncThrowingVoidSuccess()
    {
        var worker = new AsyncThrowingWorker("void-thrower");
        await WithTimeout(worker.AsyncThrowingVoidAsync(false), DefaultAsyncTimeout);
        TestLogger.Info("AsyncThrowingWorker.AsyncThrowingVoid(false) completed without error");
    }

    public async Task TestAsyncThrowingVoidThrows()
    {
        var worker = new AsyncThrowingWorker("void-thrower");
        try
        {
            await WithTimeout(worker.AsyncThrowingVoidAsync(true), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"AsyncThrowingVoid(true) threw SwiftException: {ex.Message}");
        }
    }

    public async Task TestAsyncStaticThrowingSuccess()
    {
        var result = await WithTimeout(AsyncThrowingWorker.AsyncStaticThrowingAsync(false), DefaultAsyncTimeout);
        AssertEqual("success", result, "AsyncStaticThrowing(false) should return 'success'");
        TestLogger.Info($"AsyncThrowingWorker.AsyncStaticThrowingAsync(false) = {result}");
    }

    public async Task TestAsyncStaticThrowingThrows()
    {
        try
        {
            await WithTimeout(AsyncThrowingWorker.AsyncStaticThrowingAsync(true), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"AsyncStaticThrowing(true) threw SwiftException: {ex.Message}");
        }
    }

    #endregion
}
