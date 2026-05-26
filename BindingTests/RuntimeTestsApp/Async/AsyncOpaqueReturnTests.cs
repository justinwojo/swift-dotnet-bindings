// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
}
