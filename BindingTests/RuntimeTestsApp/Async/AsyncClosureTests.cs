// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Session C: non-throwing async-closure bridge for the baseline shape
/// <c>@escaping (Args) async -&gt; T</c> with BitwiseCopyable primitive return.
/// Mirrors <see cref="AsyncThrowingClosureTests"/> for the throwing variant
/// but without an error channel — unhandled managed exceptions here are
/// routed through <c>Environment.FailFast</c>.
/// </summary>
public class AsyncClosureTests : TestBase
{
    public AsyncClosureTests(TestResults results) : base(results) { }

    public async Task TestBaselineAsyncClosureReturnsValue()
    {
        Func<Task<int>> userLambda = () => Task.FromResult(42);
        var result = await WithTimeout(
            Functions.CallAsyncClosureAsync(userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "Baseline non-throwing async closure should return 42");
        TestLogger.Info($"AsyncClosure.HappyPath = {result}");
    }

    /// <summary>
    /// Invokes the SAME closure value twice within a single outer Swift call.
    /// Covers the case where the adapter must build a fresh
    /// <c>CheckedContinuation&lt;T, Never&gt;</c> and continuation box on each
    /// await, rather than reusing stale per-invocation state.
    /// </summary>
    public async Task TestBaselineAsyncClosureTwice()
    {
        int callCount = 0;
        Func<Task<int>> userLambda = () =>
        {
            int n = System.Threading.Interlocked.Increment(ref callCount);
            return Task.FromResult(n);
        };

        var result = await WithTimeout(
            Functions.CallAsyncClosureTwiceAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(2, callCount, "Closure should have been invoked exactly twice");
        AssertEqual(3, result, "Sum of 1 + 2 should be 3");
        TestLogger.Info($"AsyncClosure.Twice sum={result} callCount={callCount}");
    }

    /// <summary>
    /// Arity-1 primitive arg: validates the Start-thunk marshals the Int32
    /// synchronously before Task.Run captures it (non-throwing path).
    /// </summary>
    public async Task TestAsyncClosureWithParamPassesArg()
    {
        Func<int, Task<int>> userLambda = v => Task.FromResult(v * 3);
        var result = await WithTimeout(
            Functions.CallAsyncClosureWithParamAsync(14, userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "OneArg non-throwing closure should return 14 * 3");
        TestLogger.Info($"AsyncClosure.WithParam = {result}");
    }

    /// <summary>
    /// Arity-3 mixed (Int32, String, AsyncClosureArgBox) on the non-throwing
    /// path: confirms the String + class arg categories marshal correctly
    /// through the Session C bridge, not just primitives. The Swift class arg
    /// round-trips via Unmanaged.passUnretained → Arc.Retain → MarshalFromSwift
    /// just as on the throwing variant.
    /// </summary>
    public async Task TestAsyncClosureThreeArgsMixedRoundTrips()
    {
        using var originalBox = new AsyncClosureArgBox(tag: 99);

        Func<int, string, AsyncClosureArgBox, Task<int>> userLambda = (n, tag, box) =>
            Task.FromResult(n + tag.Length + box.Tag);

        var result = await WithTimeout(
            Functions.CallAsyncClosureThreeArgsAsync(10, "xyz", originalBox, userLambda),
            DefaultAsyncTimeout);

        AssertEqual(10 + 3 + 99, result, "ThreeArgs non-throwing closure should return n + len(tag) + box.Tag");
        TestLogger.Info($"AsyncClosure.ThreeArgs = {result}");
    }

    /// <summary>
    /// Non-throwing async closures have no Swift error channel — an unhandled
    /// managed exception inside the closure must trigger
    /// <c>Environment.FailFast</c>. Asserting this requires running the
    /// offending code in a subprocess so the host process survives; the iOS
    /// runtime-test runner does not support that. Tracked here so coverage
    /// shows the behaviour is intentionally policy-only in this harness.
    /// </summary>
    [Skip("FailFast on unhandled managed exception in non-throwing async closure requires a subprocess harness; not feasible from the in-process iOS test runner.")]
    public Task TestAsyncClosure_UnhandledException_FailsFast()
    {
        return Task.CompletedTask;
    }
}
