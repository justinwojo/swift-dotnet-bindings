// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Swift.Runtime;

namespace Swift.SwiftUI;

/// <summary>
/// Represents SwiftUI.Text - a view that displays one or more lines of text.
/// </summary>
/// <remarks>
/// SwiftUI.Text predates the supplement's deployment floor on every platform, so the
/// type needs no <see cref="SupportedOSPlatformAttribute"/> version annotation. The
/// type itself is also fully usable on Mac Catalyst — its metadata and the
/// payload-marshalling path (<c>NewFromPayload</c>) bind there — so the supplement's
/// factory registration legitimately references it on Catalyst. The single Catalyst
/// restriction is <see cref="Create(string)"/>: <c>SBW_SwiftUI_Text_Create</c> is not
/// exported in the macabi dylib, so that one method throws
/// <see cref="PlatformNotSupportedException"/> there. The attribute is therefore on
/// <see cref="Create(string)"/>, not the type — matching the runtime guard exactly and
/// the convention the generated Apple types already follow.
/// </remarks>
public sealed class Text : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<Text> _payload = SwiftSafeHandle<Text>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<Text> Payload
    {
        get
        {
            ThrowIfDisposed();
            return _payload;
        }
    }

    /// <summary>
    /// Blittable stand-in for the frozen layout of <c>SwiftUI.Text</c>: four machine words —
    /// its multi-payload storage enum plus the array of modifiers applied to it.
    /// </summary>
    /// <remarks>
    /// Managed code never reads the fields — only size and alignment matter. Swift passes a
    /// frozen <c>Text</c> directly rather than through a pointer, so bindings that take one as
    /// a parameter pass this struct by value.
    /// </remarks>
    public struct Buffer
    {
#pragma warning disable CS0169
        private IntPtr _word0;
        private IntPtr _word1;
        private IntPtr _word2;
        private IntPtr _word3;
#pragma warning restore CS0169
    }

    /// <summary>
    /// Pins the payload for the duration of a call and exposes it as the by-value
    /// <see cref="Buffer"/> the Swift ABI expects. Dispose to release the pin.
    /// </summary>
    public unsafe PayloadBuffer<Text.Buffer> PayloadBuffer => new PayloadBuffer<Text.Buffer>(Payload);

    IntPtr ISwiftObject.SwiftHandle
    {
        get
        {
            ThrowIfDisposed();
            return _payload.DangerousGetHandle();
        }
    }

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Text(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Adopt;

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
    [UnsupportedOSPlatform("maccatalyst")]
    public static unsafe Text Create(string content)
    {
        if (OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("SwiftUI.Text construction is not available on Mac Catalyst (SBW_SwiftUI_Text_Create not exported in macabi dylib).");

        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();

        // NativeMemory rather than Marshal.AllocHGlobal: on the success path the buffer is
        // handed to SwiftSafeHandle, which always releases it with NativeMemory.Free, so the
        // allocation has to come from the matching allocator.
        IntPtr handle = (IntPtr)NativeMemory.Alloc((nuint)metadata.Size);

        // The Swift value is live in the buffer the moment the shim returns, so anything that
        // throws after that point owes it a VWT-equivalent destroy before the buffer is freed —
        // Text holds a refcounted storage box and an array of modifiers.
        bool initialized = false;
        try
        {
            var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(content);
            fixed (byte* ptr = utf8Bytes)
            {
                NativeMethods.TextCreate((IntPtr)ptr, utf8Bytes.Length, handle);
            }
            initialized = true;
            return new Text(handle);
        }
        catch
        {
            if (initialized)
                NativeMethods.TextDestroy(handle);
            NativeMemory.Free((void*)handle);
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

        // Destroys the Swift value in a buffer without freeing the buffer, so a failure between
        // construction and hand-off to SwiftSafeHandle can release what the shim already built.
        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Text_Destroy")]
        public static extern void TextDestroy(IntPtr bufferPtr);
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
