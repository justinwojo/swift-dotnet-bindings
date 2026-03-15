// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// UIImage is only available on iOS and tvOS (UIKit).
// Use explicit positive check to ensure base net10.0 target gets stubs.
#if IOS || TVOS

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents UIKit.UIImage - an object that manages image data in your app (iOS/tvOS only).
/// https://developer.apple.com/documentation/uikit/uiimage
/// </summary>
/// <remarks>
/// UIKit.UIImage is a class in Swift/Objective-C, so we wrap it with a handle-based approach.
/// This type is only available on iOS and tvOS.
/// </remarks>
public sealed class UIImage : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<UIImage> _payload = SwiftSafeHandle<UIImage>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<UIImage> Payload => _payload;

    /// <summary>
    /// Gets the size of the image.
    /// </summary>
    public CGSize Size => PInvoke_GetSize(this);

    /// <summary>
    /// Gets the scale factor of the image.
    /// </summary>
    public double Scale => PInvoke_GetScale(this);

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new UIImage(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for UIImage and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private UIImage(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<UIImage>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.UIKit, EntryPoint = "$sSo7UIImageCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.UIKit, EntryPoint = "$sSo7UIImageC4sizeSo6CGSizeVvg")]
    private static extern CGSize PInvoke_GetSize(UIImage image);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.UIKit, EntryPoint = "$sSo7UIImageC5scaleSdvg")]
    private static extern double PInvoke_GetScale(UIImage image);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the UIImage and releases any resources.
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

#else // Stub for non-iOS/tvOS platforms (macOS, generic net10.0)

using Swift.Runtime;

namespace Swift;

/// <summary>
/// Stub for UIImage on non-iOS/tvOS platforms. UIImage is only available on iOS/tvOS.
/// This stub exists to allow code that references UIImage to compile on other platforms.
/// </summary>
public sealed class UIImage : ISwiftObject, ISwiftStruct, IDisposable
{
    private UIImage() => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");

    public SwiftSafeHandle<UIImage> Payload => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");

    static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");
    IntPtr ISwiftObject.SwiftHandle => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>() => throw new PlatformNotSupportedException("UIImage is only available on iOS/tvOS.");
    public void Dispose() { }
}

#endif // IOS || TVOS
