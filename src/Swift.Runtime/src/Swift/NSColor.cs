// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// NSColor is only available on macOS/Mac Catalyst (AppKit).
// Use explicit positive check to ensure base net10.0 target gets stubs.
#if MACOS || MACCATALYST

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents AppKit.NSColor - an object that stores color data and optionally opacity (alpha value) (macOS only).
/// https://developer.apple.com/documentation/appkit/nscolor
/// </summary>
/// <remarks>
/// AppKit.NSColor is a class in Swift/Objective-C, so we wrap it with a handle-based approach.
/// This type is only available on macOS.
/// </remarks>
public sealed class NSColor : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<NSColor> _payload = SwiftSafeHandle<NSColor>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<NSColor> Payload => _payload;

    /// <summary>
    /// Gets the black color.
    /// </summary>
    public static NSColor Black => PInvoke_GetBlack();

    /// <summary>
    /// Gets the white color.
    /// </summary>
    public static NSColor White => PInvoke_GetWhite();

    /// <summary>
    /// Gets the clear (transparent) color.
    /// </summary>
    public static NSColor Clear => PInvoke_GetClear();

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new NSColor(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for NSColor and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private NSColor(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<NSColor>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSColorCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSColorC5blackABvgZ")]
    private static extern NSColor PInvoke_GetBlack();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSColorC5whiteABvgZ")]
    private static extern NSColor PInvoke_GetWhite();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.AppKit, EntryPoint = "$sSo7NSColorC5clearABvgZ")]
    private static extern NSColor PInvoke_GetClear();

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the NSColor and releases any resources.
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
/// Stub for NSColor on non-macOS platforms. NSColor is only available on macOS/Mac Catalyst.
/// This stub exists to allow code that references NSColor to compile on other platforms.
/// </summary>
public sealed class NSColor : ISwiftObject, ISwiftStruct, IDisposable
{
    private NSColor() => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");

    public SwiftSafeHandle<NSColor> Payload => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");

    public static NSColor Black => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    public static NSColor White => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    public static NSColor Clear => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");

    static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    IntPtr ISwiftObject.SwiftHandle => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() => throw new PlatformNotSupportedException("NSColor is only available on macOS/Mac Catalyst.");
    public void Dispose() { }
}

#endif // MACOS || MACCATALYST
