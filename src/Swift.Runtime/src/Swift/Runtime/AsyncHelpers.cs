// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via Arc.Retain()
    /// before calling async Swift methods. Must be released via Arc.Release() after callback.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct RetainedSelfPtr
    {
        public readonly IntPtr Ptr;
        public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
    }

    /// <summary>
    /// Wraps a SafeHandle that needs DangerousRelease() called after async completion.
    /// Used for async instance methods on structs where the SafeHandle must stay alive
    /// until the Swift async operation completes.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct DeferredSafeHandleRelease
    {
        public readonly SafeHandle Handle;
        public DeferredSafeHandleRelease(SafeHandle handle) => Handle = handle;
    }

    /// <summary>
    /// Wraps a copy buffer pointer with its TypeMetadata for proper cleanup.
    /// Used for non-frozen struct parameters in async operations.
    /// Destroy must be called before freeing the buffer to release Swift references.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct CopyBufferWithType
    {
        public readonly IntPtr Buffer;
        public readonly TypeMetadata Metadata;
        public CopyBufferWithType(IntPtr buffer, TypeMetadata metadata)
        {
            Buffer = buffer;
            Metadata = metadata;
        }
    }

    /// <summary>
    /// Wraps a CancellationTokenRegistration for disposal in async callbacks.
    /// Stored in the async holder array so the callback can dispose the registration
    /// after completion, cancellation, or error.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct CancellationRegistrationHolder
    {
        public readonly System.Threading.CancellationTokenRegistration Registration;
        public readonly System.Threading.CancellationToken Token;
        public CancellationRegistrationHolder(System.Threading.CancellationTokenRegistration registration, System.Threading.CancellationToken token)
        {
            Registration = registration;
            Token = token;
        }
    }
}
