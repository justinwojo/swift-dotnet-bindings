// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents CoreImage.CIContext - an evaluation context for rendering image processing results and performing image analysis.
/// https://developer.apple.com/documentation/coreimage/cicontext
/// </summary>
/// <remarks>
/// CoreImage.CIContext is a class in Swift/Objective-C, so we wrap it with a handle-based approach.
/// </remarks>
public sealed class CIContext : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<CIContext> _payload = SwiftSafeHandle<CIContext>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<CIContext> Payload => _payload;

    /// <summary>
    /// Creates a new CIContext with default options.
    /// </summary>
    public CIContext()
    {
        var context = PInvoke_Init();
        _payload = context._payload;
    }

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new CIContext(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for CIContext and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private CIContext(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<CIContext>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.CoreImage, EntryPoint = "$sSo9CIContextCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.CoreImage, EntryPoint = "$sSo9CIContextCABycfC")]
    private static extern CIContext PInvoke_Init();

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the CIContext and releases any resources.
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
