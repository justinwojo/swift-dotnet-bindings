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
    /// <param name="handle">The GCHandle to the closure state. Intentionally leaked — see remarks.</param>
    /// <param name="state">The closure state containing the async function.</param>
    /// <param name="continuationBoxPtr">Pointer to Swift's continuation box.</param>
    /// <param name="successAction">Callback to invoke on success with (boxPtr, resultPtr).</param>
    /// <param name="errorAction">Callback to invoke on error with (boxPtr, errorMsgPtr).</param>
    /// <remarks>
    /// The GCHandle is intentionally NOT freed. Async closures share the escaping-closure
    /// leak semantics documented at <c>WrapperEmitter.Marshalling.cs</c>: Swift may retain
    /// the closure context and invoke it more than once (e.g. retry, fan-out), so freeing
    /// after a single invocation would leave Swift with a dangling GCHandle.
    /// A leak-free model via an explicit Swift-side release callback is tracked as a
    /// post-1.0 improvement.
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
    /// <param name="handle">The GCHandle to the closure state. Intentionally leaked — see <see cref="RunAsync{T}"/> remarks.</param>
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

    // ---- Per-arity arg-bearing overloads (Session B) ----
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
