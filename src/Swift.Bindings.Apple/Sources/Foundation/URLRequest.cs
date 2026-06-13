// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Minimal stub: exists only to satisfy ISwiftObject constraint in bound generic type arguments
// (e.g., SwiftResult<URLRequest, Error>). All public API surface uses Foundation.NSUrlRequest
// via ObjCBridgeableProjection — Swift's Foundation.URLRequest bridges 1:1 to NSURLRequest so
// consumers get the familiar Microsoft.iOS ObjC type instead of a second managed URLRequest.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift.Foundation;

/// <summary>
/// Minimal ISwiftObject stub for Foundation.URLRequest, used only as a generic type argument
/// in bound generic types. Public API uses Foundation.NSUrlRequest directly.
/// </summary>
public sealed class URLRequest : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<URLRequest> _payload = SwiftSafeHandle<URLRequest>.Zero;
    private bool _disposed;
    private static TypeMetadata? _cachedMetadata;

    /// <summary>The safe handle wrapping the native Swift storage for this URLRequest.</summary>
    public SwiftSafeHandle<URLRequest> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
        => _cachedMetadata ??= PInvoke_GetMetadata();

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        // handle may point to transient memory (stackalloc in SwiftResult, etc.).
        // Must heap-copy via InitializeWithCopy before wrapping in SwiftSafeHandle
        // (which calls NativeMemory.Free on dispose).
        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();
        unsafe
        {
            var size = (int)metadata.Size;
            var heapCopy = NativeMemory.Alloc((nuint)size);
            metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, (void*)handle, metadata);
            return new URLRequest((IntPtr)heapCopy);
        }
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
            throw new ArgumentException($"Span size mismatch: expected {(int)metadata.Size}, got {swiftDestSpan.Length}");
        unsafe
        {
            fixed (void* dest = swiftDestSpan)
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(dest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        => throw new SwiftRuntimeException($"Protocol conformance not implemented for URLRequest and {typeof(TProtocol).Name}");

    private URLRequest(IntPtr handle) => _payload = new SwiftSafeHandle<URLRequest>(handle);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    /// <summary>Releases the native Swift storage backing this URLRequest.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _payload.Dispose();
            _disposed = true;
        }
    }
}
