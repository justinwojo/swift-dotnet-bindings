// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Dispatch.DispatchQueue - an object that manages the execution of tasks on the app's main thread or on a background thread.
/// https://developer.apple.com/documentation/dispatch/dispatchqueue
/// </summary>
/// <remarks>
/// Dispatch.DispatchQueue is a class in Swift, so we wrap it with a handle-based approach.
/// </remarks>
public sealed class DispatchQueue : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<DispatchQueue> _payload = SwiftSafeHandle<DispatchQueue>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<DispatchQueue> Payload => _payload;

    /// <summary>
    /// Gets the main dispatch queue associated with the main thread.
    /// </summary>
    public static DispatchQueue Main => PInvoke_GetMain();

    /// <summary>
    /// Gets a global concurrent queue with the specified quality of service.
    /// </summary>
    public static DispatchQueue Global() => PInvoke_GetGlobal();

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new DispatchQueue(handle);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
    {
        throw new SwiftRuntimeException($"Protocol conformance not implemented for DispatchQueue and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private DispatchQueue(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<DispatchQueue>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftDispatch, EntryPoint = "$sSo17OS_dispatch_queueCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftDispatch, EntryPoint = "$s8Dispatch0A5QueueC4mainACvgZ")]
    private static extern DispatchQueue PInvoke_GetMain();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftDispatch, EntryPoint = "$s8Dispatch0A5QueueC6globalACyFZ")]
    private static extern DispatchQueue PInvoke_GetGlobal();

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the DispatchQueue and releases any resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _payload.Dispose();
            _disposed = true;
        }
    }

    #endregion
}
