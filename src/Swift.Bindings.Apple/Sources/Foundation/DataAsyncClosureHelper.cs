// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift.Foundation;

/// <summary>
/// Apple-package helper for async closures that return Swift.Foundation.Data.
/// Lives here (not in Runtime) because Data is Apple-owned after the supplement exodus.
/// </summary>
public static class DataAsyncClosureHelper
{
    /// <summary>
    /// Runs an async closure that returns <see cref="Data"/>, converts the result to a byte
    /// array, and calls the success callback with the pinned bytes. The GCHandle is
    /// intentionally NOT freed — async closures share the escaping-closure leak semantics of
    /// <see cref="Swift.Runtime.AsyncClosureHelper"/>: Swift may retain the closure context
    /// and invoke it more than once, so freeing after a single invocation would leave Swift
    /// with a dangling GCHandle.
    /// </summary>
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
                var result = await state.AsyncFunc();
                var bytes = result.ToByteArray();
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
        });
    }
}
