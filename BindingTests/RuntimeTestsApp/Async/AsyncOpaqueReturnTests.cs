// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for AsyncOpaqueWorker — methods that are async and/or throwing AND return an opaque
/// type (`some Describable`). Regression coverage for the opaque-return emission gate: such a
/// method must be emitted ONLY by the async harness (which boxes the opaque return into an
/// `any Describable` existential), never also by the thin synchronous `@_silgen_name` alias,
/// which would double-define the shared symbol and fail to compile. This is the exact shape of
/// AppIntents `perform() async throws -> some IntentResult`. The compile gate proves the
/// double-emit is gone; these runtime checks prove the surviving async/throwing path marshals
/// the boxed opaque return back to C# correctly.
/// </summary>
public class AsyncOpaqueReturnTests : TestBase
{
    public AsyncOpaqueReturnTests(TestResults results) : base(results) { }

    // async -> some Describable
    public async Task TestMakeOpaqueAsync()
    {
        var worker = new AsyncOpaqueWorker();
        var result = await WithTimeout(worker.MakeOpaqueAsync("alpha"), DefaultAsyncTimeout);
        AssertNotNull(result, "MakeOpaqueAsync returned non-null IDescribable");
        AssertEqual("[async-opaque] alpha", result.GetDescribe(), "async opaque describe()");
        TestLogger.Info($"AsyncOpaqueWorker.MakeOpaqueAsync() = {result.GetDescribe()}");
    }

    // async throws -> some Describable (the AppIntents perform() shape)
    public async Task TestMakeOpaqueAsyncThrowing()
    {
        var worker = new AsyncOpaqueWorker();
        var result = await WithTimeout(worker.MakeOpaqueAsyncThrowingAsync("beta"), DefaultAsyncTimeout);
        AssertNotNull(result, "MakeOpaqueAsyncThrowing returned non-null IDescribable");
        AssertEqual("[async-throwing-opaque] beta", result.GetDescribe(), "async throwing opaque describe()");
        TestLogger.Info($"AsyncOpaqueWorker.MakeOpaqueAsyncThrowingAsync() = {result.GetDescribe()}");
    }

    // throws -> some Describable (non-async): synchronous path still emits a callable method.
    public void TestMakeOpaqueThrowing()
    {
        var worker = new AsyncOpaqueWorker();
        var result = worker.MakeOpaqueThrowing("gamma");
        AssertNotNull(result, "MakeOpaqueThrowing returned non-null IDescribable");
        AssertEqual("[throwing-opaque] gamma", result.GetDescribe(), "throwing opaque describe()");
        TestLogger.Info($"AsyncOpaqueWorker.MakeOpaqueThrowing() = {result.GetDescribe()}");
    }

    // ----- Async owned-return ARC leak probes -----
    //
    // The async harness boxes a `some Renderable` return into an `any Renderable` existential at
    // +1 (initializeMemory into the carrier) and the C# completion callback constructs the proxy
    // with `ownsContainer: true`. Disposing the proxy must value-witness Destroy the adopted
    // container, ARC-releasing the inline `TrackedRenderable` payload. Without the async
    // ownsContainer wiring each call orphans the payload's +1 (a leaked instance per call). These
    // mirror the SYNC probes in ExistentialReturnLeakProbeTests on the async-harness path. The
    // dispose loops live in `[MethodImpl(NoInlining)]` async helpers so the completed state machine
    // (which holds the awaited proxy local) is collectible before the leak assertion. Each call is
    // bounded by `WithTimeout(DefaultAsyncTimeout)` — matching the single-call async tests above —
    // so a regressed completion callback fails the probe bounded instead of hanging the run, and
    // the loop stops on the first timeout rather than leaving the remaining iterations in flight.

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// <c>async -> some Renderable</c>: the async harness boxes the opaque return to
    /// <c>any Renderable</c> at +1 and the completion callback adopts it (<c>ownsContainer</c>).
    /// Disposing each awaited proxy must release the inline tracked class payload.
    /// </summary>
    public async Task TestAsyncOpaqueReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var worker = new AsyncOpaqueWorker();
        await AllocAndDisposeTrackedRenderablesAsync(worker, 100);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async -> some Renderable must not orphan the existential payload's retain");
        TestLogger.Info("async some Renderable: 100 awaited returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeTrackedRenderablesAsync(AsyncOpaqueWorker worker, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var r = await WithTimeout(worker.MakeTrackedRenderableAsync(i), DefaultAsyncTimeout);
            (r as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>async throws -> some Renderable</c> (the AppIntents <c>perform()</c> shape): the throwing
    /// async arm of the owned-return path. Same adopt-and-release contract on the proxy as the
    /// non-throwing async return.
    /// </summary>
    public async Task TestAsyncThrowingOpaqueReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var worker = new AsyncOpaqueWorker();
        await AllocAndDisposeTrackedRenderablesThrowingAsync(worker, 100);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async throws -> some Renderable must not orphan the existential payload's retain");
        TestLogger.Info("async throws some Renderable: 100 awaited returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeTrackedRenderablesThrowingAsync(AsyncOpaqueWorker worker, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var r = await WithTimeout(worker.MakeTrackedRenderableAsyncThrowingAsync(i), DefaultAsyncTimeout);
            (r as IDisposable)?.Dispose();
        }
    }
}
