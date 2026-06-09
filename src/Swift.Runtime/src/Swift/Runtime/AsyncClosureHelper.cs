// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Helper class for executing async+throwing closures.
/// This class is NOT marked unsafe, allowing async/await to work correctly.
/// Generated code in unsafe classes calls these helpers to execute async work.
/// </summary>
public static class AsyncClosureHelper
{
    /// <summary>
    /// Runs an async closure that returns a generic type T.
    /// Marshals the result to a native buffer and calls the success callback.
    /// </summary>
    /// <typeparam name="T">The return type of the async operation.</typeparam>
    /// <param name="handle">The GCHandle to the closure state. NOT freed here — owned by the Swift-side box, see remarks.</param>
    /// <param name="state">The closure state containing the async function.</param>
    /// <param name="continuationBoxPtr">Pointer to Swift's continuation box.</param>
    /// <param name="successAction">Callback to invoke on success with (boxPtr, resultPtr).</param>
    /// <param name="errorAction">Callback to invoke on error with (boxPtr, errorMsgPtr).</param>
    /// <remarks>
    /// This helper deliberately does NOT free <paramref name="handle"/>: it runs once per
    /// Swift invocation of the closure, and Swift may invoke the same context more than
    /// once (e.g. two sequential <c>await closure()</c> legs), so a per-invocation free
    /// would dangle a later leg. Ownership of the handle instead rides on the Swift-side
    /// <c>_SBClosureCtx</c> owner-token box wrapping the context pointer (emitted into the
    /// async wrapper's <c>_SBW_AsyncClosureHandoff.ctxOwner</c>): when Swift ARC releases
    /// the adapter closure, the box's deinit upcalls <see cref="SwiftClosureContext"/>'s
    /// free trampoline and releases the handle exactly once. When
    /// libSwiftBindingsRuntime is absent the box degrades to a no-deinit fallback and the
    /// handle leaks as it did before — matching the sync escaping-closure contract.
    /// </remarks>
    public static void RunAsync<T>(
        GCHandle handle,
        AsyncThrowingClosureState<T> state,
        IntPtr continuationBoxPtr,
        Action<IntPtr, IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc();
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>
    /// Runs an async closure that returns void (Task).
    /// Calls the success callback when complete or error callback on failure.
    /// </summary>
    /// <param name="handle">The GCHandle to the closure state. NOT freed here — owned by the Swift-side box, see <see cref="RunAsync{T}"/> remarks.</param>
    /// <param name="state">The closure state containing the async function.</param>
    /// <param name="continuationBoxPtr">Pointer to Swift's continuation box.</param>
    /// <param name="successAction">Callback to invoke on success with (boxPtr).</param>
    /// <param name="errorAction">Callback to invoke on error with (boxPtr, errorMsgPtr).</param>
    public static void RunVoidAsync(
        GCHandle handle,
        AsyncThrowingClosureStateVoid state,
        IntPtr continuationBoxPtr,
        Action<IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await state.AsyncFunc();
                successAction(continuationBoxPtr);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    // ---- Per-arity arg-bearing overloads ----
    // Args must be marshaled to managed values by the caller BEFORE invoking these
    // helpers; Swift-owned pointers die the moment the Start thunk returns. The
    // helpers then spawn Task.Run and call state.AsyncFunc(args...) on the pool.

    /// <summary>Runs a single-arg async closure returning T.</summary>
    public static void RunAsync<A0, T>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        Action<IntPtr, IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a single-arg async closure returning void.</summary>
    public static void RunVoidAsync<A0>(
        GCHandle handle,
        AsyncThrowingClosureStateVoid<A0> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        Action<IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await state.AsyncFunc(a0);
                successAction(continuationBoxPtr);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a two-arg async closure returning T.</summary>
    public static void RunAsync<A0, A1, T>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        Action<IntPtr, IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a two-arg async closure returning void.</summary>
    public static void RunVoidAsync<A0, A1>(
        GCHandle handle,
        AsyncThrowingClosureStateVoid<A0, A1> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        Action<IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await state.AsyncFunc(a0, a1);
                successAction(continuationBoxPtr);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a three-arg async closure returning T.</summary>
    public static void RunAsync<A0, A1, A2, T>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, A2, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        Action<IntPtr, IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a three-arg async closure returning void.</summary>
    public static void RunVoidAsync<A0, A1, A2>(
        GCHandle handle,
        AsyncThrowingClosureStateVoid<A0, A1, A2> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        Action<IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await state.AsyncFunc(a0, a1, a2);
                successAction(continuationBoxPtr);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a four-arg async closure returning T.</summary>
    public static void RunAsync<A0, A1, A2, A3, T>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, A2, A3, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        A3 a3,
        Action<IntPtr, IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2, a3);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a four-arg async closure returning void.</summary>
    public static void RunVoidAsync<A0, A1, A2, A3>(
        GCHandle handle,
        AsyncThrowingClosureStateVoid<A0, A1, A2, A3> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        A3 a3,
        Action<IntPtr> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await state.AsyncFunc(a0, a1, a2, a3);
                successAction(continuationBoxPtr);
            }
            catch (Exception ex)
            {
                ReportError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    // ---- Non-throwing variants ----
    // Mirror the throwing RunAsync family for @escaping (...) async -> T closures.
    // Key difference: the Swift closure has no error channel, so a C# exception
    // cannot surface as a `throws` resume. Explicit try/catch -> Environment.FailFast
    // is required; unobserved Task exceptions only surface via TaskScheduler events
    // and do not reliably crash the process.

    /// <summary>Runs a zero-arg non-throwing async closure returning T.</summary>
    public static void RunAsyncNonThrowing<T>(
        GCHandle handle,
        AsyncClosureState<T> state,
        IntPtr continuationBoxPtr,
        Action<IntPtr, IntPtr> successAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc();
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                FailFastNonThrowing(ex);
            }
        });
    }

    /// <summary>Runs a single-arg non-throwing async closure returning T.</summary>
    public static void RunAsyncNonThrowing<A0, T>(
        GCHandle handle,
        AsyncClosureState<A0, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        Action<IntPtr, IntPtr> successAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                FailFastNonThrowing(ex);
            }
        });
    }

    /// <summary>Runs a two-arg non-throwing async closure returning T.</summary>
    public static void RunAsyncNonThrowing<A0, A1, T>(
        GCHandle handle,
        AsyncClosureState<A0, A1, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        Action<IntPtr, IntPtr> successAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                FailFastNonThrowing(ex);
            }
        });
    }

    /// <summary>Runs a three-arg non-throwing async closure returning T.</summary>
    public static void RunAsyncNonThrowing<A0, A1, A2, T>(
        GCHandle handle,
        AsyncClosureState<A0, A1, A2, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        Action<IntPtr, IntPtr> successAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                FailFastNonThrowing(ex);
            }
        });
    }

    /// <summary>Runs a four-arg non-throwing async closure returning T.</summary>
    public static void RunAsyncNonThrowing<A0, A1, A2, A3, T>(
        GCHandle handle,
        AsyncClosureState<A0, A1, A2, A3, T> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        A3 a3,
        Action<IntPtr, IntPtr> successAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2, a3);
                CompleteWithResult(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                FailFastNonThrowing(ex);
            }
        });
    }

    private static void FailFastNonThrowing(Exception ex)
    {
        Environment.FailFast(
            $"Unhandled exception in non-throwing async closure: {ex}", ex);
    }

    // Shared success/error completion paths — marshal T into a native buffer and
    // fire the success callback; pin a UTF-8 error message and fire the error
    // callback. Kept local to avoid duplicating the boilerplate across 9 helpers.

    private static void CompleteWithResult<T>(T result, IntPtr continuationBoxPtr, Action<IntPtr, IntPtr> successAction)
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        try
        {
            unsafe
            {
                var resultBuffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                try
                {
                    var resultSpan = new Span<byte>((void*)resultBuffer, (int)metadata.Size);
                    SwiftMarshal.MarshalToSwift(result, ref resultSpan);
                    successAction(continuationBoxPtr, resultBuffer);
                }
                finally
                {
                    NativeMemory.Free((void*)resultBuffer);
                }
            }
        }
        finally
        {
            (result as IDisposable)?.Dispose();
        }
    }

    private static void ReportError(Exception ex, IntPtr continuationBoxPtr, Action<IntPtr, IntPtr> errorAction)
    {
        var errorBytes = System.Text.Encoding.UTF8.GetBytes(ex.Message + "\0");
        var pinnedBytes = GCHandle.Alloc(errorBytes, GCHandleType.Pinned);
        try
        {
            errorAction(continuationBoxPtr, pinnedBytes.AddrOfPinnedObject());
        }
        finally
        {
            pinnedBytes.Free();
        }
    }
}
