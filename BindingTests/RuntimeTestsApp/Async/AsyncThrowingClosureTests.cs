// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Session A: generator-emitted baseline async-closure bridge.
/// Matches the hand-written Session 0 spike shape in
/// <c>AsyncClosureSpikeTests</c>, but exercises the wrapper produced by the
/// emitter for <c>callAsyncThrowingClosure(_ closure: @escaping () async throws -&gt; Int32) async throws -&gt; Int32</c>.
/// </summary>
public class AsyncThrowingClosureTests : TestBase
{
    public AsyncThrowingClosureTests(TestResults results) : base(results) { }

    public async Task TestBaselineAsyncClosureReturns42()
    {
        Func<Task<int>> userLambda = () => Task.FromResult(42);
        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "Baseline async-throwing closure should return 42");
        TestLogger.Info($"AsyncThrowingClosure.HappyPath = {result}");
    }

    public async Task TestBaselineAsyncClosurePropagatesError()
    {
        Func<Task<int>> userLambda = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("baseline-boom");
        };

        try
        {
            await WithTimeout(
                Functions.CallAsyncThrowingClosureAsync(userLambda),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("baseline-boom"))
                throw new AssertionException($"Expected 'baseline-boom' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncThrowingClosure.ErrorPath threw SwiftException: {ex.Message}");
        }
    }

    /// <summary>
    /// Invoke the baseline bridge repeatedly to validate GCHandle lifetime —
    /// AsyncClosureHelper.RunAsync intentionally leaks the context handle
    /// (matches sync escaping-closure semantics). A leak-induced crash would
    /// show up as double-free / wrong-handle-target errors on the Nth call.
    /// </summary>
    public async Task TestBaselineAsyncClosureMultipleInvocations()
    {
        const int iterations = 16;
        var sum = 0;
        for (int i = 0; i < iterations; i++)
        {
            int local = i;
            Func<Task<int>> userLambda = () => Task.FromResult(local * 2);
            var result = await WithTimeout(
                Functions.CallAsyncThrowingClosureAsync(userLambda),
                DefaultAsyncTimeout);
            AssertEqual(local * 2, result, $"Iteration {local} should return {local * 2}");
            sum += result;
        }
        AssertEqual((iterations - 1) * iterations, sum,
            "Sum of 2*i for i in [0..iterations) should match arithmetic series");
        TestLogger.Info($"AsyncThrowingClosure.MultiInvoke sum={sum}");
    }

    /// <summary>
    /// Invokes the SAME closure value twice within a single outer Swift call.
    /// The per-iteration loop above only exercises single-invoke adapter
    /// lifetime; this covers the case where the adapter must build a fresh
    /// <c>CheckedContinuation</c> and continuation box on each await, rather
    /// than reusing stale per-invocation state.
    /// </summary>
    public async Task TestBaselineAsyncClosureSameClosureInvokedTwice()
    {
        int callCount = 0;
        Func<Task<int>> userLambda = () =>
        {
            int n = System.Threading.Interlocked.Increment(ref callCount);
            return Task.FromResult(n);
        };

        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureTwiceAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(2, callCount, "Closure should have been invoked exactly twice");
        AssertEqual(3, result, "Sum of 1 + 2 should be 3");
        TestLogger.Info($"AsyncThrowingClosure.SameClosureTwice sum={result} callCount={callCount}");
    }
}
