// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async methods on generic types (callback hoisting) and async tuple returns
/// with generic elements (GenericContext threading through the async tuple pipeline).
///
/// Part A: AsyncTupleWorker — non-generic async tuple returns (regression).
/// Part B: AsyncGenericContainer — async methods on generic types with hoisted callbacks.
/// </summary>
public class AsyncGenericTupleTests : TestBase
{
    public AsyncGenericTupleTests(TestResults results) : base(results) { }

    #region AsyncTupleWorker — Non-generic async tuple regression

    public async Task TestAsyncTupleWorker_IntPair()
    {
        var worker = new AsyncTupleWorker("worker");
        var (a, b) = await WithTimeout(worker.FetchIntPairAsync(), DefaultAsyncTimeout);
        AssertEqual(10, a, "First element should be 10");
        AssertEqual(20, b, "Second element should be 20");
        TestLogger.Info($"AsyncTupleWorker.FetchIntPair() = ({a}, {b})");
    }

    public async Task TestAsyncTupleWorker_LabeledPair()
    {
        var worker = new AsyncTupleWorker("hello");
        var (label, number) = await WithTimeout(worker.FetchLabeledPairAsync(), DefaultAsyncTimeout);
        AssertEqual("hello", label, "Label should match constructor value");
        AssertEqual(42, number, "Number should be 42");
        TestLogger.Info($"AsyncTupleWorker.FetchLabeledPair() = ('{label}', {number})");
    }

    #endregion

    #region AsyncGenericContainer — Async on generic types (hoisted callbacks)

    // AsyncGenericContainer<T> requires T : ISwiftObject (Swift generic class).
    // Note: fetchPair() returning (T, Int32) is correctly skipped — its return type
    // references the parent generic param T, which [UnmanagedCallersOnly] callbacks can't handle.
    //
    // The non-generic async tuple pipeline (AsyncTupleWorker above) works end-to-end.
    // These generic-type async methods compile correctly at the C# level but fail at
    // runtime because @_silgen_name on a generic type's extension method is stripped by
    // the Swift compiler — it cannot export a fixed symbol for a function that depends on
    // generic specialization. Fixing this requires @_cdecl wrappers with explicit type
    // metadata forwarding, which is separate from the non-generic async tuple work.

    [Skip("Generic type async: @_silgen_name on generic extension stripped by Swift compiler — needs @_cdecl with explicit type metadata forwarding")]
    public async Task TestAsyncGenericContainer_ProcessAsync()
    {
        var container = new AsyncGenericContainer<NumberItem>(new NumberItem(42));
        var result = await WithTimeout(container.ProcessAsync(), DefaultAsyncTimeout);
        AssertEqual(42, result, "ProcessAsync should return 42");
        TestLogger.Info($"AsyncGenericContainer<NumberItem>.ProcessAsync() = {result}");
    }

    [Skip("Generic type async: @_silgen_name on generic extension stripped by Swift compiler — needs @_cdecl with explicit type metadata forwarding")]
    public async Task TestAsyncGenericContainer_FetchOrThrow_Success()
    {
        var container = new AsyncGenericContainer<NumberItem>(new NumberItem(0));
        var result = await WithTimeout(container.FetchOrThrowAsync(false), DefaultAsyncTimeout);
        AssertEqual(77, result, "FetchOrThrow(false) should return 77");
        TestLogger.Info($"AsyncGenericContainer<NumberItem>.FetchOrThrow(false) = {result}");
    }

    [Skip("Generic type async: @_silgen_name on generic extension stripped by Swift compiler — needs @_cdecl with explicit type metadata forwarding")]
    public async Task TestAsyncGenericContainer_FetchOrThrow_Throws()
    {
        var container = new AsyncGenericContainer<NumberItem>(new NumberItem(0));
        try
        {
            await WithTimeout(container.FetchOrThrowAsync(true), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"FetchOrThrow(true) threw SwiftException: {ex.Message}");
        }
    }

    #endregion
}
