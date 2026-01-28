// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
public sealed class NSColor : ISwiftObject, IDisposable
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
