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
    /// Runs an async closure that returns Swift.Data.
    /// Converts the result to a byte array and calls the success callback with the pinned bytes.
    /// </summary>
    /// <param name="handle">The GCHandle to the closure state (will be freed on completion).</param>
    /// <param name="state">The closure state containing the async function.</param>
    /// <param name="continuationBoxPtr">Pointer to Swift's continuation box.</param>
    /// <param name="successAction">Callback to invoke on success with (boxPtr, dataPtr, length).</param>
    /// <param name="errorAction">Callback to invoke on error with (boxPtr, errorMsgPtr).</param>
    public static void RunDataAsync(
        GCHandle handle,
        AsyncThrowingClosureState<Data> state,
        IntPtr continuationBoxPtr,
        Action<IntPtr, IntPtr, nint> successAction,
        Action<IntPtr, IntPtr> errorAction)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Await the user's async function to get Swift.Data
                var result = await state.AsyncFunc();

                // Convert Swift.Data to byte array
                var bytes = result.ToByteArray();

                // Pin the bytes and call Swift's success callback
                // Swift will copy the data, so we can release the pin after the callback returns
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
            catch (Exception ex)
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
            finally
            {
                handle.Free();
            }
        });
    }

    /// <summary>
    /// Runs an async closure that returns a generic type T.
    /// Marshals the result to a native buffer and calls the success callback.
    /// </summary>
    /// <typeparam name="T">The return type of the async operation.</typeparam>
    /// <param name="handle">The GCHandle to the closure state (will be freed on completion).</param>
    /// <param name="state">The closure state containing the async function.</param>
    /// <param name="continuationBoxPtr">Pointer to Swift's continuation box.</param>
    /// <param name="successAction">Callback to invoke on success with (boxPtr, resultPtr).</param>
    /// <param name="errorAction">Callback to invoke on error with (boxPtr, errorMsgPtr).</param>
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

                // Marshal result to native buffer and call Swift's success callback
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
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
            catch (Exception ex)
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
            finally
            {
                handle.Free();
            }
        });
    }

    /// <summary>
    /// Runs an async closure that returns void (Task).
    /// Calls the success callback when complete or error callback on failure.
    /// </summary>
    /// <param name="handle">The GCHandle to the closure state (will be freed on completion).</param>
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
            finally
            {
                handle.Free();
            }
        });
    }
}
