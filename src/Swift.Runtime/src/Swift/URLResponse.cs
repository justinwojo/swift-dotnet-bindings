// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Foundation.URLResponse type - metadata associated with the response to a URL load request.
/// https://developer.apple.com/documentation/foundation/urlresponse
/// </summary>
/// <remarks>
/// Foundation.URLResponse is a class in Swift, wrapping an Objective-C class (NSURLResponse).
/// </remarks>
public sealed class URLResponse : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<URLResponse> _payload = SwiftSafeHandle<URLResponse>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// This returns a SafeHandle for P/Invoke compatibility.
    /// </summary>
    public SwiftSafeHandle<URLResponse> Payload => _payload;

    /// <summary>
    /// Gets the URL for this response.
    /// </summary>
    public URL? URL
    {
        get
        {
            return PInvoke_GetURL(this);
        }
    }

    /// <summary>
    /// Gets the MIME type of the response.
    /// </summary>
    public string? MIMEType
    {
        get
        {
            var swiftString = PInvoke_GetMIMEType(this);
            return swiftString?.ToString();
        }
    }

    /// <summary>
    /// Gets the expected content length of the response data.
    /// Returns -1 if the length is unknown.
    /// </summary>
    public long ExpectedContentLength
    {
        get => PInvoke_GetExpectedContentLength(this);
    }

    /// <summary>
    /// Gets the name of the text encoding for the response, if available.
    /// </summary>
    public string? TextEncodingName
    {
        get
        {
            var swiftString = PInvoke_GetTextEncodingName(this);
            return swiftString?.ToString();
        }
    }

    /// <summary>
    /// Gets the suggested filename for the response data.
    /// </summary>
    public string? SuggestedFilename
    {
        get
        {
            var swiftString = PInvoke_GetSuggestedFilename(this);
            return swiftString?.ToString();
        }
    }

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new URLResponse(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for URLResponse and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    private URLResponse(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<URLResponse>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    // URLResponse wraps NSURLResponse, which is an Objective-C class
    // The mangled names use the "So" prefix for Swift-imported Objective-C types
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseCMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseC3URLSo5NSURLCSgvg")]
    private static extern URL? PInvoke_GetURL(URLResponse response);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseC8MIMETypeSSSgvg")]
    private static extern SwiftString? PInvoke_GetMIMEType(URLResponse response);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseC21expectedContentLengths5Int64Vvg")]
    private static extern long PInvoke_GetExpectedContentLength(URLResponse response);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseC16textEncodingNameSSSgvg")]
    private static extern SwiftString? PInvoke_GetTextEncodingName(URLResponse response);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseC17suggestedFilenameSSSgvg")]
    private static extern SwiftString? PInvoke_GetSuggestedFilename(URLResponse response);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the URLResponse and releases any resources.
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
    /// Returns a string representation of the URLResponse.
    /// </summary>
    public override string ToString() => URL?.AbsoluteString ?? "<no URL>";
}
