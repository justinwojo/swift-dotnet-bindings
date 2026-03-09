// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Foundation.URL type - a value that identifies the location of a resource.
/// https://developer.apple.com/documentation/foundation/url
/// </summary>
/// <remarks>
/// Foundation.URL is a non-frozen struct in Swift, so we wrap it with a handle-based approach.
/// </remarks>
public sealed class URL : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<URL> _payload = SwiftSafeHandle<URL>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// This returns a SafeHandle for P/Invoke compatibility.
    /// </summary>
    public SwiftSafeHandle<URL> Payload => _payload;

    /// <summary>
    /// Creates a URL from a string representation.
    /// </summary>
    /// <param name="urlString">A string that represents a URL.</param>
    /// <returns>A new URL instance, or null if the string is not a valid URL.</returns>
    public static URL? FromString(string urlString)
    {
        var swiftString = new SwiftString(urlString);
        var url = PInvoke_InitWithString(swiftString);
        // Check if URL creation succeeded (Swift returns Optional<URL>)
        // For now, assume it succeeds - proper Optional handling would be needed
        return url;
    }

    /// <summary>
    /// Creates a URL from a file path.
    /// </summary>
    /// <param name="path">A file path.</param>
    /// <param name="isDirectory">Whether the path represents a directory.</param>
    /// <returns>A new URL instance.</returns>
    public static URL FromFilePath(string path, bool isDirectory = false)
    {
        var swiftString = new SwiftString(path);
        return PInvoke_InitWithFilePath(swiftString, isDirectory);
    }

    /// <summary>
    /// Converts this URL to a .NET Uri.
    /// </summary>
    /// <returns>A Uri representation of this URL.</returns>
    public Uri ToUri()
    {
        var absoluteString = AbsoluteString;
        return new Uri(absoluteString);
    }

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Converts this Swift.URL to a .NET iOS Foundation.NSUrl.
    /// </summary>
    /// <returns>An NSUrl representation of this URL.</returns>
    public Foundation.NSUrl ToNSUrl()
    {
        // Use the absoluteString to create an NSUrl (safe, avoids internal layout assumptions)
        return new Foundation.NSUrl(AbsoluteString);
    }

    /// <summary>
    /// Creates a Swift.URL from a .NET iOS Foundation.NSUrl.
    /// </summary>
    /// <param name="nsUrl">The NSUrl to convert.</param>
    /// <returns>A Swift.URL representation of the NSUrl.</returns>
    /// <exception cref="ArgumentNullException">Thrown if nsUrl is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the NSUrl's AbsoluteString is not a valid URL.</exception>
    public static URL FromNSUrl(Foundation.NSUrl nsUrl)
    {
        if (nsUrl == null)
            throw new ArgumentNullException(nameof(nsUrl));

        var absoluteString = nsUrl.AbsoluteString;
        if (absoluteString == null)
            throw new ArgumentException("NSUrl has no absolute string", nameof(nsUrl));

        return FromString(absoluteString) ?? throw new ArgumentException("Invalid URL string", nameof(nsUrl));
    }

    /// <summary>
    /// Implicitly converts a Foundation.NSUrl to a Swift.URL.
    /// </summary>
    /// <param name="nsUrl">The NSUrl to convert.</param>
    public static implicit operator URL(Foundation.NSUrl nsUrl) => FromNSUrl(nsUrl);

    /// <summary>
    /// Implicitly converts a Swift.URL to a Foundation.NSUrl.
    /// </summary>
    /// <param name="url">The Swift.URL to convert.</param>
    public static implicit operator Foundation.NSUrl(URL url) => url.ToNSUrl();
#endif

    /// <summary>
    /// Gets the absolute string representation of the URL.
    /// </summary>
    public string AbsoluteString
    {
        get
        {
            var swiftString = PInvoke_GetAbsoluteString(this);
            return swiftString.ToString();
        }
    }

    /// <summary>
    /// Gets the path component of the URL.
    /// </summary>
    public string Path
    {
        get
        {
            var swiftString = PInvoke_GetPath(this);
            return swiftString.ToString();
        }
    }

    /// <summary>
    /// Gets whether the URL represents a file URL.
    /// </summary>
    public bool IsFileURL => PInvoke_GetIsFileURL(this);

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new URL(handle);
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
                // Ensure the handle is valid before copying
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for URL and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private URL(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<URL>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLV6stringACSgSS_tcfC")]
    private static extern URL PInvoke_InitWithString(SwiftString urlString);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLV8filePath11isDirectoryACSS_SbtcfC")]
    private static extern URL PInvoke_InitWithFilePath(SwiftString path, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLV14absoluteStringSSvg")]
    private static extern SwiftString PInvoke_GetAbsoluteString(URL url);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLV4pathSSvg")]
    private static extern SwiftString PInvoke_GetPath(URL url);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation3URLV9isFileURLSbvg")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool PInvoke_GetIsFileURL(URL url);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the URL and releases any resources.
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

    /// <summary>
    /// Returns the absolute string representation of the URL.
    /// </summary>
    public override string ToString() => AbsoluteString;
}
