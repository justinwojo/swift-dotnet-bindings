// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Session 0 ABI proof for the async-closure bridge. Hand-written P/Invoke
/// + Start thunk + outer async callbacks, mirroring what the emitter will
/// generate in Session A (see src/docs/async-closure-plan.md §3.7).
///
/// The Swift side lives in Sources/SwiftBindingsTestLib/Async/AsyncClosureSpike.swift.
/// </summary>
public partial class AsyncClosureSpikeTests : TestBase
{
    public AsyncClosureSpikeTests(TestResults results) : base(results) { }

    public async Task TestSpikeHappyPathReturns42()
    {
        Func<Task<int>> userLambda = () => Task.FromResult(42);
        var result = await WithTimeout(SpikeBridge.SpikeCallAsyncOpAsync(userLambda), DefaultAsyncTimeout);
        AssertEqual(42, result, "Spike happy path should return 42 from the user lambda");
        TestLogger.Info($"AsyncClosureSpike.HappyPath = {result}");
    }

    public async Task TestSpikeErrorPathPropagatesMessage()
    {
        Func<Task<int>> userLambda = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        };

        try
        {
            await WithTimeout(SpikeBridge.SpikeCallAsyncOpAsync(userLambda), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("boom"))
                throw new AssertionException($"Expected 'boom' in error message, got: {ex.Message}");
            TestLogger.Info($"AsyncClosureSpike.ErrorPath threw SwiftException: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------
    // P/Invoke + callback plumbing — models the emitter output exactly.
    // ----------------------------------------------------------------------
    private static unsafe partial class SpikeBridge
    {
        /// <summary>
        /// Public spike API: models the generated outer-async method that
        /// takes a C# `Func&lt;Task&lt;int&gt;&gt;` and returns `Task&lt;int&gt;`.
        /// </summary>
        public static Task<int> SpikeCallAsyncOpAsync(Func<Task<int>> op)
        {
            var state = new AsyncThrowingClosureState<int> { AsyncFunc = op };
            var contextHandle = GCHandle.Alloc(state);

            var tcs = new TaskCompletionSource<int>();
            var taskHandle = GCHandle.Alloc(tcs);

            PInvoke_spike_callAsyncOp(
                s_outerCallback,
                s_outerErrorCallback,
                GCHandle.ToIntPtr(taskHandle),
                GCHandle.ToIntPtr(contextHandle),
                s_opStartFunc);

            return tcs.Task;
        }

        // ---- Outer async completion callbacks (Task<int> result path) ----

        private static readonly delegate* unmanaged[Cdecl]<int, IntPtr, void> s_outerCallback = &OuterComplete;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> s_outerErrorCallback = &OuterError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OuterComplete(int result, IntPtr task)
        {
            var handle = GCHandle.FromIntPtr(task);
            try
            {
                if (handle.Target is TaskCompletionSource<int> tcs)
                    tcs.TrySetResult(result);
            }
            finally { handle.Free(); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OuterError(IntPtr errorMessagePtr, int isCancellation, IntPtr task)
        {
            var handle = GCHandle.FromIntPtr(task);
            try
            {
                if (handle.Target is TaskCompletionSource<int> tcs)
                {
                    if (isCancellation != 0)
                    {
                        tcs.TrySetCanceled();
                    }
                    else
                    {
                        var message = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                        tcs.TrySetException(new SwiftException(message));
                    }
                }
            }
            finally { handle.Free(); }
        }

        // ---- Inner start thunk — routes Swift's "please run my closure now" call
        //      into C# Task.Run via AsyncClosureHelper.RunAsync.

        private static readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> s_opStartFunc = &OpStart;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OpStart(IntPtr contextPtr, IntPtr continuationBoxPtr, IntPtr successFP, IntPtr errorFP)
        {
            var handle = GCHandle.FromIntPtr(contextPtr);
            if (handle.Target is not AsyncThrowingClosureState<int> state)
                return;

            var successAction = new Action<IntPtr, IntPtr>((box, resultPtr) =>
            {
                var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFP;
                fp(box, resultPtr);
            });
            var errorAction = new Action<IntPtr, IntPtr>((box, errPtr) =>
            {
                var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFP;
                fp(box, errPtr);
            });

            AsyncClosureHelper.RunAsync(handle, state, continuationBoxPtr, successAction, errorAction);
        }

        // ---- P/Invoke into the hand-written Swift wrapper ----

        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        [LibraryImport("SwiftBindingsTestLib", EntryPoint = "spike_callAsyncOp")]
        private static partial void PInvoke_spike_callAsyncOp(
            delegate* unmanaged[Cdecl]<int, IntPtr, void> callback,
            delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> errorCallback,
            IntPtr taskHandle,
            IntPtr opContextPtr,
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> opStartFunc);
    }
}
