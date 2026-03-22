// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;

namespace Swift;

/// <summary>
/// Represents Foundation.URLRequest type - a URL load request that is independent of protocol or URL scheme.
/// https://developer.apple.com/documentation/foundation/urlrequest
/// </summary>
/// <remarks>
/// Foundation.URLRequest is a non-frozen struct in Swift, so we wrap it with a handle-based approach.
/// Property access and mutating methods use CallConvSwift with SwiftSelf.
/// </remarks>
public sealed class URLRequest : ISwiftObject, ISwiftStruct, IDisposable
{
    private SwiftSafeHandle<URLRequest> _payload = SwiftSafeHandle<URLRequest>.Zero;
    private bool _disposed;
    private URL? _constructionUrl;

    private static TypeMetadata? _cachedMetadata;

    private static TypeMetadata GetMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

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
    public static unsafe URLRequest FromURL(URL url)
    {
        var metadata = GetMetadata();
        IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
        try
        {
            PInvoke_InitWithURL(new SwiftIndirectResult((void*)buffer), url.Payload);
            var request = new URLRequest(buffer, ownsBuffer: true);
            request._constructionUrl = url;
            return request;
        }
        catch
        {
            NativeMemory.Free((void*)buffer);
            throw;
        }
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
    public URL? URL => _constructionUrl;

    /// <summary>
    /// Gets or sets the HTTP request method.
    /// </summary>
    public unsafe string? HTTPMethod
    {
        get
        {
            var result = PInvoke_GetHTTPMethod(
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
            return result?.ToString();
        }
        set
        {
            if (value != null)
            {
                using var swiftString = new SwiftString(value);
                using var buffer = swiftString.PayloadBuffer;
                PInvoke_SetHTTPMethod(buffer.Buffer,
                    new SwiftSelf((void*)_payload.DangerousGetHandle()));
            }
            else
            {
                // Setting httpMethod to nil resets it to the default ("GET") in Foundation.
                // Optional<String>.none must be passed via VWT bit representation.
                using var none = SwiftOptional<SwiftString>.NewNone();
                bool success = false;
                none.Payload.DangerousAddRef(ref success);
                try
                {
                    var noneBuffer = *(SwiftString.Buffer*)none.Payload.DangerousGetHandle();
                    PInvoke_SetHTTPMethod(noneBuffer,
                        new SwiftSelf((void*)_payload.DangerousGetHandle()));
                }
                finally
                {
                    if (success)
                        none.Payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the timeout interval for the request.
    /// </summary>
    public unsafe double TimeoutInterval
    {
        get
        {
            return PInvoke_GetTimeoutInterval(
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
        }
        set
        {
            PInvoke_SetTimeoutInterval(value,
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
        }
    }

    /// <summary>
    /// Sets the value for an HTTP header field.
    /// If a value was previously set for the given header field, that value is replaced.
    /// Pass null to remove the header.
    /// </summary>
    /// <param name="value">The new value for the header field, or null to remove it.</param>
    /// <param name="forHTTPHeaderField">The name of the header field.</param>
    public unsafe void SetValue(string? value, string forHTTPHeaderField)
    {
        using var fieldSwift = new SwiftString(forHTTPHeaderField);
        using var fieldBuffer = fieldSwift.PayloadBuffer;

        if (value != null)
        {
            // Non-null case: pass the string value directly.
            // Optional<String>.some has the same bit representation as String
            // due to extra inhabitant encoding.
            using var valueSwift = new SwiftString(value);
            using var valueBuffer = valueSwift.PayloadBuffer;
            PInvoke_SetValue(valueBuffer.Buffer, fieldBuffer.Buffer,
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
        }
        else
        {
            // Null case: construct Optional<String>.none representation via VWT.
            using var none = SwiftOptional<SwiftString>.NewNone();
            bool success = false;
            none.Payload.DangerousAddRef(ref success);
            try
            {
                var noneBuffer = *(SwiftString.Buffer*)none.Payload.DangerousGetHandle();
                PInvoke_SetValue(noneBuffer, fieldBuffer.Buffer,
                    new SwiftSelf((void*)_payload.DangerousGetHandle()));
            }
            finally
            {
                if (success)
                    none.Payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Adds an HTTP header value. Unlike SetValue, this appends to any existing values
    /// for the specified header field (comma-separated).
    /// </summary>
    /// <param name="value">The value for the header field.</param>
    /// <param name="forHTTPHeaderField">The name of the header field.</param>
    public unsafe void AddValue(string value, string forHTTPHeaderField)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        using var valueSwift = new SwiftString(value);
        using var valueBuffer = valueSwift.PayloadBuffer;
        using var fieldSwift = new SwiftString(forHTTPHeaderField);
        using var fieldBuffer = fieldSwift.PayloadBuffer;
        PInvoke_AddValue(valueBuffer.Buffer, fieldBuffer.Buffer,
            new SwiftSelf((void*)_payload.DangerousGetHandle()));
    }

    /// <summary>
    /// Returns the value for a specific HTTP header field.
    /// </summary>
    /// <param name="forHTTPHeaderField">The name of the header field.</param>
    /// <returns>The value for the header field, or null if no value is set.</returns>
    public unsafe string? Value(string forHTTPHeaderField)
    {
        using var fieldSwift = new SwiftString(forHTTPHeaderField);
        using var fieldBuffer = fieldSwift.PayloadBuffer;
        var result = PInvoke_GetValue(fieldBuffer.Buffer,
            new SwiftSelf((void*)_payload.DangerousGetHandle()));
        return result?.ToString();
    }

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Converts this Swift.URLRequest to a .NET iOS Foundation.NSUrlRequest.
    /// Copies URL, HTTP method, timeout, and all HTTP headers.
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
    /// Copies URL, HTTP method, and timeout.
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

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new URLRequest(handle, ownsBuffer: false);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = GetMetadata();
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for URLRequest and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructors

    /// <summary>
    /// Creates a URLRequest from a native buffer.
    /// If ownsBuffer is true, takes ownership (from FromURL where buffer was just created).
    /// If ownsBuffer is false, copies the data (from NewFromPayload where buffer is transient).
    /// </summary>
    private unsafe URLRequest(IntPtr handle, bool ownsBuffer)
    {
        if (ownsBuffer)
        {
            _payload = new SwiftSafeHandle<URLRequest>(handle);
        }
        else
        {
            var metadata = GetMetadata();
            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
            _payload = new SwiftSafeHandle<URLRequest>(bufferPtr);
        }
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    // Constructor: init(url: URL)
    // Non-frozen struct init returns via indirect result. URL param passed via SafeHandle (pointer).
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV3urlAcA3URLV_tcfC")]
    private static extern void PInvoke_InitWithURL(SwiftIndirectResult result, SafeHandle url);

    // Property getter: var httpMethod: String?
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV10httpMethodSSSgvg")]
    private static extern SwiftString? PInvoke_GetHTTPMethod(SwiftSelf self);

    // Property setter: var httpMethod: String? { set }
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV10httpMethodSSSgvs")]
    private static extern void PInvoke_SetHTTPMethod(SwiftString.Buffer method, SwiftSelf self);

    // Property getter: var timeoutInterval: Double
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV15timeoutIntervalSdvg")]
    private static extern double PInvoke_GetTimeoutInterval(SwiftSelf self);

    // Property setter: var timeoutInterval: Double { set }
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV15timeoutIntervalSdvs")]
    private static extern void PInvoke_SetTimeoutInterval(double interval, SwiftSelf self);

    // Mutating method: setValue(_ value: String?, forHTTPHeaderField field: String)
    // Optional<String> uses extra inhabitant encoding (same size as String)
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV8setValue_18forHTTPHeaderFieldySSSg_SStF")]
    private static extern void PInvoke_SetValue(SwiftString.Buffer value, SwiftString.Buffer field, SwiftSelf self);

    // Mutating method: addValue(_ value: String, forHTTPHeaderField field: String)
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV8addValue_18forHTTPHeaderFieldySS_SStF")]
    private static extern void PInvoke_AddValue(SwiftString.Buffer value, SwiftString.Buffer field, SwiftSelf self);

    // Method: value(forHTTPHeaderField field: String) -> String?
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$s10Foundation10URLRequestV5value18forHTTPHeaderFieldSSSgSS_tF")]
    private static extern SwiftString? PInvoke_GetValue(SwiftString.Buffer field, SwiftSelf self);

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
