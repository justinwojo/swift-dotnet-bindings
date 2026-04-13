// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;

namespace SwiftUI;

/// <summary>
/// Represents SwiftUI.Text - a view that displays one or more lines of text.
/// </summary>
public sealed class Text : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<Text> _payload = SwiftSafeHandle<Text>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<Text> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Text(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for Text and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Construction

    internal Text(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<Text>(handle);
    }

    /// <summary>
    /// Creates a new SwiftUI.Text displaying the specified string content.
    /// </summary>
    /// <param name="content">The text content to display.</param>
    /// <returns>A new Text instance.</returns>
    public static unsafe Text Create(string content)
    {
        if (OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("SwiftUI.Text construction is not available on Mac Catalyst (SBW_SwiftUI_Text_Create not exported in macabi dylib).");

        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();
        var size = (int)metadata.Size;
        var handle = Marshal.AllocHGlobal(size);

        try
        {
            var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(content);
            fixed (byte* ptr = utf8Bytes)
            {
                NativeMethods.TextCreate((IntPtr)ptr, utf8Bytes.Length, handle);
            }
            return new Text(handle);
        }
        catch
        {
            Marshal.FreeHGlobal(handle);
            throw;
        }
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftUI, EntryPoint = "$s7SwiftUI4TextVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    private static class NativeMethods
    {
        private const string RuntimeLib = "SwiftBindingsRuntime";

        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Text_Create")]
        public static extern void TextCreate(IntPtr utf8Ptr, nint utf8Len, IntPtr outBufferPtr);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes and releases any resources.
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
