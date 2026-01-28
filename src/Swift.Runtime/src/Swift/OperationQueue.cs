// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Foundation.OperationQueue - a queue that regulates the execution of operations.
/// https://developer.apple.com/documentation/foundation/operationqueue
/// </summary>
/// <remarks>
/// Foundation.OperationQueue is a class in Swift/Objective-C, so we wrap it with a handle-based approach.
/// </remarks>
public sealed class OperationQueue : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<OperationQueue> _payload = SwiftSafeHandle<OperationQueue>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<OperationQueue> Payload => _payload;

    /// <summary>
    /// Gets the main operation queue associated with the main thread.
    /// </summary>
    public static OperationQueue Main => PInvoke_GetMain();

    /// <summary>
    /// Gets the operation queue associated with the current thread.
    /// </summary>
    public static OperationQueue? Current => PInvoke_GetCurrent();

    /// <summary>
    /// Creates a new operation queue.
    /// </summary>
    public OperationQueue()
    {
        var queue = PInvoke_Init();
        _payload = queue._payload;
    }

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new OperationQueue(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for OperationQueue and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private OperationQueue(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<OperationQueue>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo16NSOperationQueueCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo16NSOperationQueueCABycfC")]
    private static extern OperationQueue PInvoke_Init();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo16NSOperationQueueC4mainABvgZ")]
    private static extern OperationQueue PInvoke_GetMain();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo16NSOperationQueueC7currentABSgvgZ")]
    private static extern OperationQueue? PInvoke_GetCurrent();

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the OperationQueue and releases any resources.
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
