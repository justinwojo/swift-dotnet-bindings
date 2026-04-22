// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Runtime helper for async closures whose return type is Swift.String. Lives in
/// Swift.Runtime (not Apple) because Swift.String is runtime-owned.
///
/// ABI parity with <c>Swift.Foundation.DataAsyncClosureHelper</c>: the success
/// callback receives <c>(boxPtr, bytesPtr, length)</c> of pinned UTF-8 bytes and the
/// Swift side constructs a <c>String</c> from them. The error callback receives a
/// pinned UTF-8 C-string exception message.
///
/// The GCHandle is intentionally NOT freed — async closures share the escaping-closure
/// leak semantics of <see cref="AsyncClosureHelper"/>: Swift may retain the closure
/// context and invoke it more than once, so freeing after a single invocation would
/// leave Swift with a dangling GCHandle.
/// </summary>
public static class StringAsyncClosureHelper
{
    /// <summary>Runs a zero-arg async-throwing closure returning <see cref="string"/>.</summary>
    public static void RunStringAsync(
        GCHandle handle,
        AsyncThrowingClosureState<string> state,
        IntPtr continuationBoxPtr,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc();
                CompleteWithString(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportStringError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a single-arg async-throwing closure returning <see cref="string"/>.</summary>
    public static void RunStringAsync<A0>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, string> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0);
                CompleteWithString(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportStringError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a two-arg async-throwing closure returning <see cref="string"/>.</summary>
    public static void RunStringAsync<A0, A1>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, string> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1);
                CompleteWithString(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportStringError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a three-arg async-throwing closure returning <see cref="string"/>.</summary>
    public static void RunStringAsync<A0, A1, A2>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, A2, string> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2);
                CompleteWithString(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportStringError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    /// <summary>Runs a four-arg async-throwing closure returning <see cref="string"/>.</summary>
    public static void RunStringAsync<A0, A1, A2, A3>(
        GCHandle handle,
        AsyncThrowingClosureState<A0, A1, A2, A3, string> state,
        IntPtr continuationBoxPtr,
        A0 a0,
        A1 a1,
        A2 a2,
        A3 a3,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await state.AsyncFunc(a0, a1, a2, a3);
                CompleteWithString(result, continuationBoxPtr, successAction);
            }
            catch (Exception ex)
            {
                ReportStringError(ex, continuationBoxPtr, errorAction);
            }
        });
    }

    private static void CompleteWithString(string result, IntPtr continuationBoxPtr, Action<IntPtr, IntPtr, nint> successAction)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(result ?? string.Empty);
        var pinnedBytes = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            successAction(continuationBoxPtr, pinnedBytes.AddrOfPinnedObject(), bytes.Length);
        }
        finally
        {
            pinnedBytes.Free();
        }
    }

    private static void ReportStringError(Exception ex, IntPtr continuationBoxPtr, Action<IntPtr, IntPtr> errorAction)
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
