// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// End-to-end gate for the S13 Pillar C real async reverse-dispatch witness (Finding 36).
///
/// Shape: a class-bound Swift protocol <c>AsyncReverseCompute</c> with a single primitive-shaped
/// <c>func compute(_:) async throws -&gt; Int32</c> requirement, satisfied by a C# conformer. A
/// forward Swift async driver (<c>callAsyncReverseCompute</c>) reverse-dispatches into that
/// conformer, so each test round-trips
///   C# → [forward async bridge] → Swift driver → [real reverse-async witness] → C# impl
///       → [continuation resume] → back to the awaiting C#.
///
/// What this proves that the legacy blocking witness could not:
///   • HAPPY — the witness genuinely suspends on <c>withCheckedThrowingContinuation</c> and
///     resumes with the C# impl's value (not a thread-blocked <c>.GetAwaiter().GetResult()</c>).
///   • ERROR — a C# impl that throws resumes the Swift continuation WITH the error (the legacy
///     sync slot had no Swift error channel and could only FailFast the process); the error
///     surfaces back in C# as a <see cref="SwiftException"/>.
///   • CANCELLATION — an <see cref="OperationCanceledException"/> from the C# impl routes through
///     the same resume-with-error path rather than terminating the process.
///
/// One code path, no per-runtime branch — runs on Mono (simulator), CoreCLR (macOS) and
/// NativeAOT (device).
/// </summary>
public class AsyncReverseWitnessTests : TestBase
{
    public AsyncReverseWitnessTests(TestResults results) : base(results) { }

    /// <summary>
    /// Happy path: the reverse-async witness suspends and resumes with the impl's value.
    /// </summary>
    public async Task TestReverseAsyncWitnessHappyPath()
    {
        var impl = new AsyncReverseComputeImpl((n, _) => Task.FromResult(n * 2));
        var result = await WithTimeout(
            Functions.CallAsyncReverseComputeAsync(impl, 21),
            DefaultAsyncTimeout);
        AssertEqual(42, result,
            "Real reverse-async witness should suspend and resume with the C# impl's value");
        TestLogger.Info($"AsyncReverseWitness.HappyPath = {result}");
    }

    /// <summary>
    /// The impl is awaited (not invoked synchronously): a deferred completion still resumes the
    /// continuation. A genuine yield before producing the value would deadlock the legacy
    /// thread-blocked slot but completes cleanly through the continuation handoff.
    /// </summary>
    public async Task TestReverseAsyncWitnessDeferredCompletion()
    {
        var impl = new AsyncReverseComputeImpl(async (n, _) =>
        {
            await Task.Yield();
            return n + 100;
        });
        var result = await WithTimeout(
            Functions.CallAsyncReverseComputeAsync(impl, 5),
            DefaultAsyncTimeout);
        AssertEqual(105, result,
            "Real reverse-async witness should resume after the impl's awaited continuation");
        TestLogger.Info($"AsyncReverseWitness.Deferred = {result}");
    }

    /// <summary>
    /// Error path: a throwing C# impl resumes the Swift continuation WITH the error, which
    /// propagates back through the forward async bridge as a <see cref="SwiftException"/>.
    /// </summary>
    public async Task TestReverseAsyncWitnessPropagatesError()
    {
        var impl = new AsyncReverseComputeImpl(async (n, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("reverse-boom");
        });

        try
        {
            await WithTimeout(
                Functions.CallAsyncReverseComputeAsync(impl, 7),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("reverse-boom"))
                throw new AssertionException($"Expected 'reverse-boom' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncReverseWitness.ErrorPath threw SwiftException: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancellation path: an <see cref="OperationCanceledException"/> from the C# impl routes
    /// through the same resume-with-error channel (it does not FailFast the process), surfacing
    /// back in C# as a <see cref="SwiftException"/>.
    /// </summary>
    public async Task TestReverseAsyncWitnessPropagatesCancellation()
    {
        var impl = new AsyncReverseComputeImpl(async (n, _) =>
        {
            await Task.Yield();
            throw new OperationCanceledException("reverse-cancelled");
        });

        try
        {
            await WithTimeout(
                Functions.CallAsyncReverseComputeAsync(impl, 9),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("reverse-cancelled"))
                throw new AssertionException($"Expected 'reverse-cancelled' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncReverseWitness.CancelPath threw SwiftException: {ex.Message}");
        }
    }

    /// <summary>
    /// Repeated invocations: each suspend/resume builds a fresh continuation box and resumes it
    /// exactly once. A leaked or double-resumed box would surface as a crash or wrong result on
    /// the Nth call.
    /// </summary>
    public async Task TestReverseAsyncWitnessMultipleInvocations()
    {
        const int iterations = 16;
        var sum = 0;
        for (int i = 0; i < iterations; i++)
        {
            int local = i;
            var impl = new AsyncReverseComputeImpl((n, _) => Task.FromResult(n + local));
            var result = await WithTimeout(
                Functions.CallAsyncReverseComputeAsync(impl, local),
                DefaultAsyncTimeout);
            AssertEqual(local * 2, result, $"Iteration {local} should return {local * 2}");
            sum += result;
        }
        AssertEqual((iterations - 1) * iterations, sum,
            "Sum of 2*i for i in [0..iterations) should match arithmetic series");
        TestLogger.Info($"AsyncReverseWitness.MultiInvoke sum={sum}");
    }
}

// C# conformer for the Swift protocol AsyncReverseCompute. The async requirement
// `func compute(_:) async throws -> Int32` projects to `Task<int> ComputeAsync(int,
// CancellationToken = default)`. Behaviour is supplied per test via the delegate so a single
// conformer exercises the happy, deferred, error and cancellation arms.
internal sealed class AsyncReverseComputeImpl : IAsyncReverseCompute
{
    private readonly Func<int, System.Threading.CancellationToken, Task<int>> _body;

    public AsyncReverseComputeImpl(Func<int, System.Threading.CancellationToken, Task<int>> body)
    {
        _body = body;
    }

    public Task<int> ComputeAsync(int n, System.Threading.CancellationToken cancellationToken = default)
        => _body(n, cancellationToken);
}
