// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Foundation.URLRequest type - a URL load request that is independent of protocol or URL scheme.
/// https://developer.apple.com/documentation/foundation/urlrequest
/// </summary>
/// <remarks>
/// Foundation.URLRequest is a non-frozen struct in Swift, so we wrap it with a handle-based approach.
/// </remarks>
public sealed class URLRequest : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<URLRequest> _payload = SwiftSafeHandle<URLRequest>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// This returns a SafeHandle for P/Invoke compatibility.
    /// </summary>
    public SwiftSafeHandle<URLRequest> Payload => _payload;

    /// <summary>
    /// Creates a URLRequest from a URL.
    /// </summary>
    /// <param name="url">The URL for the request.</param>
    /// <returns>A new URLRequest instance.</returns>
    public static URLRequest FromURL(URL url)
    {
        return PInvoke_InitWithURL(url);
    }

    /// <summary>
    /// Creates a URLRequest from a string URL.
    /// </summary>
    /// <param name="urlString">A string that represents a URL.</param>
    /// <returns>A new URLRequest instance, or null if the URL string is invalid.</returns>
    public static URLRequest? FromString(string urlString)
    {
        var url = URL.FromString(urlString);
        if (url == null)
            return null;
        return FromURL(url);
    }

    /// <summary>
    /// Gets the URL of the request.
    /// </summary>
    public URL? URL
    {
        get
        {
            // Note: URLRequest.url returns Optional<URL>
            // For simplicity, we return URL directly and handle null internally
            return PInvoke_GetURL(this);
        }
    }

    /// <summary>
    /// Gets or sets the HTTP request method.
    /// </summary>
    public string? HTTPMethod
    {
        get
        {
            var swiftString = PInvoke_GetHTTPMethod(this);
            return swiftString?.ToString();
        }
        set
        {
            if (value != null)
            {
                var swiftString = new SwiftString(value);
                PInvoke_SetHTTPMethod(this, swiftString);
            }
        }
    }

    /// <summary>
    /// Gets or sets the timeout interval for the request.
    /// </summary>
    public double TimeoutInterval
    {
        get => PInvoke_GetTimeoutInterval(this);
        set => PInvoke_SetTimeoutInterval(this, value);
    }

#if IOS || MACCATALYST || MACOS
    /// <summary>
    /// Converts this Swift.URLRequest to a .NET iOS Foundation.NSUrlRequest.
    /// </summary>
    /// <returns>An NSUrlRequest representation of this URLRequest.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this URLRequest has no URL.</exception>
    public Foundation.NSUrlRequest ToNSUrlRequest()
    {
        var url = URL?.ToNSUrl();
        if (url == null)
            throw new InvalidOperationException("URLRequest has no URL");

        var request = new Foundation.NSMutableUrlRequest(url);
        request.HttpMethod = HTTPMethod ?? "GET";
        request.TimeoutInterval = TimeoutInterval;
        return request;
    }

    /// <summary>
    /// Creates a Swift.URLRequest from a .NET iOS Foundation.NSUrlRequest.
    /// </summary>
    /// <param name="nsUrlRequest">The NSUrlRequest to convert.</param>
    /// <returns>A Swift.URLRequest representation of the NSUrlRequest.</returns>
    /// <exception cref="ArgumentNullException">Thrown if nsUrlRequest is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the NSUrlRequest has no URL.</exception>
    public static URLRequest FromNSUrlRequest(Foundation.NSUrlRequest nsUrlRequest)
    {
        if (nsUrlRequest == null)
            throw new ArgumentNullException(nameof(nsUrlRequest));

        if (nsUrlRequest.Url == null)
            throw new ArgumentException("NSUrlRequest has no URL", nameof(nsUrlRequest));

        var swiftUrl = Swift.URL.FromNSUrl(nsUrlRequest.Url);
        var request = FromURL(swiftUrl);

        if (nsUrlRequest.HttpMethod != null)
            request.HTTPMethod = nsUrlRequest.HttpMethod;

        request.TimeoutInterval = nsUrlRequest.TimeoutInterval;
        return request;
    }
#endif

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new URLRequest(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for URLRequest and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private URLRequest(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<URLRequest>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV3urlAcA3URLV_tcfC")]
    private static extern URLRequest PInvoke_InitWithURL(URL url);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV3urlAA3URLVSgvg")]
    private static extern URL? PInvoke_GetURL(URLRequest request);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV10httpMethodSSSgvg")]
    private static extern SwiftString? PInvoke_GetHTTPMethod(URLRequest request);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV10httpMethodSSSgvs")]
    private static extern void PInvoke_SetHTTPMethod(URLRequest request, SwiftString method);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV15timeoutIntervalSdvg")]
    private static extern double PInvoke_GetTimeoutInterval(URLRequest request);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV15timeoutIntervalSdvs")]
    private static extern void PInvoke_SetTimeoutInterval(URLRequest request, double interval);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the URLRequest and releases any resources.
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
    /// Returns a string representation of the URLRequest.
    /// </summary>
    public override string ToString() => URL?.AbsoluteString ?? "<no URL>";
}
