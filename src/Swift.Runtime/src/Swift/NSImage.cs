// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// NSImage is only available on macOS/Mac Catalyst (AppKit).
// Use explicit positive check to ensure base net10.0 target gets stubs.
#if MACOS || MACCATALYST

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents AppKit.NSImage - a high-level interface for manipulating image data (macOS only).
/// https://developer.apple.com/documentation/appkit/nsimage
/// </summary>
/// <remarks>
/// AppKit.NSImage is a class in Swift/Objective-C, so we wrap it with a handle-based approach.
/// This type is only available on macOS.
/// </remarks>
public sealed class NSImage : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<NSImage> _payload = SwiftSafeHandle<NSImage>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<NSImage> Payload => _payload;

    /// <summary>
    /// Gets the size of the image.
    /// </summary>
    public CGSize Size => PInvoke_GetSize(this);

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new NSImage(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for NSImage and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private NSImage(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<NSImage>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSImageCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSImageC4sizeSo6CGSizeVvg")]
    private static extern CGSize PInvoke_GetSize(NSImage image);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the NSImage and releases any resources.
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

#else // Stub for non-macOS platforms (iOS, tvOS, generic net10.0)

using Swift.Runtime;

namespace Swift;

/// <summary>
/// Stub for NSImage on non-macOS platforms. NSImage is only available on macOS/Mac Catalyst.
/// This stub exists to allow code that references NSImage to compile on other platforms.
/// </summary>
public sealed class NSImage : ISwiftObject, IDisposable
{
    private NSImage() => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");

    public SwiftSafeHandle<NSImage> Payload => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");

    static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() => throw new PlatformNotSupportedException("NSImage is only available on macOS/Mac Catalyst.");
    public void Dispose() { }
}

#endif // MACOS || MACCATALYST
