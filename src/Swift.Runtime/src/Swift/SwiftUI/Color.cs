// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Swift;
using Swift.Runtime;

namespace SwiftUI;

/// <summary>
/// Represents SwiftUI.Color - a color used for rendering.
/// </summary>
/// <remarks>
/// SwiftUI.Color predates the runtime's deployment floor on every platform, so the type
/// needs no <see cref="SupportedOSPlatformAttribute"/> version annotation, and the type
/// itself is fully usable on Mac Catalyst — its metadata resolves and the
/// payload-marshalling path binds there. The single Catalyst restriction is
/// <see cref="Create(double, double, double, double)"/>: the SwiftUI construction shims
/// are compiled out of the macabi slice of the runtime library, so that one method throws
/// <see cref="PlatformNotSupportedException"/> there. The attribute is therefore on the
/// factory, not the type.
/// </remarks>
public sealed class Color : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<Color> _payload = SwiftSafeHandle<Color>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<Color> Payload => _payload;

    /// <summary>
    /// Blittable stand-in for the frozen layout of <c>SwiftUI.Color</c>: a single 8-byte
    /// reference to its refcounted color-provider box.
    /// </summary>
    /// <remarks>
    /// Managed code never reads the field — only size and alignment matter. Swift passes a
    /// frozen <c>Color</c> directly in a register rather than through a pointer, so bindings
    /// that take one as a parameter pass this struct by value.
    /// </remarks>
    public struct Buffer
    {
#pragma warning disable CS0169
        private IntPtr _providerBox;
#pragma warning restore CS0169
    }

    /// <summary>
    /// Pins the payload for the duration of a call and exposes it as the by-value
    /// <see cref="Buffer"/> the Swift ABI expects. Dispose to release the pin.
    /// </summary>
    public unsafe PayloadBuffer<Color.Buffer> PayloadBuffer => new PayloadBuffer<Color.Buffer>(_payload);

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    #region ISwiftObject Implementation

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return _cachedMetadata ??= PInvoke_GetMetadata();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Color(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for Color and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Construction

    internal Color(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<Color>(handle);
    }

    /// <summary>
    /// Creates a SwiftUI.Color from sRGB components.
    /// </summary>
    /// <param name="red">The red component. Nominally 0...1.</param>
    /// <param name="green">The green component. Nominally 0...1.</param>
    /// <param name="blue">The blue component. Nominally 0...1.</param>
    /// <param name="opacity">The opacity. Nominally 0...1; defaults to fully opaque.</param>
    /// <returns>A new <see cref="Color"/> owning its Swift payload; dispose it when done.</returns>
    /// <remarks>
    /// Components map straight onto SwiftUI's <c>Color(red:green:blue:opacity:)</c>. Values
    /// outside 0...1 are passed through rather than clamped — SwiftUI reads them as
    /// extended-range sRGB — so only non-finite values are rejected.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A component is NaN or infinite.</exception>
    /// <exception cref="PlatformNotSupportedException">Running on Mac Catalyst.</exception>
    [UnsupportedOSPlatform("maccatalyst")]
    public static unsafe Color Create(double red, double green, double blue, double opacity = 1.0)
    {
        if (OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("SwiftUI.Color construction is not available on Mac Catalyst (SBW_SwiftUI_Color_Create is not exported in the macabi runtime slice).");

        ThrowIfNotFinite(red, nameof(red));
        ThrowIfNotFinite(green, nameof(green));
        ThrowIfNotFinite(blue, nameof(blue));
        ThrowIfNotFinite(opacity, nameof(opacity));

        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();

        // NativeMemory rather than Marshal.AllocHGlobal: on the success path the buffer is
        // handed to SwiftSafeHandle, which always releases it with NativeMemory.Free, so the
        // allocation has to come from the matching allocator.
        IntPtr handle = (IntPtr)NativeMemory.Alloc((nuint)metadata.Size);

        // The Swift value is live in the buffer the moment the shim returns, so anything
        // that throws after that point owes it a VWT-equivalent destroy before the buffer
        // is freed — Color holds a refcounted color provider.
        bool initialized = false;
        try
        {
            NativeMethods.ColorCreate(red, green, blue, opacity, handle);
            initialized = true;
            return new Color(handle);
        }
        catch
        {
            if (initialized)
                NativeMethods.ColorDestroy(handle);
            NativeMemory.Free((void*)handle);
            throw;
        }
    }

    private static void ThrowIfNotFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(paramName, value, "Color components must be finite.");
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftUI, EntryPoint = "$s7SwiftUI5ColorVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    private static class NativeMethods
    {
        private const string RuntimeLib = "SwiftBindingsRuntime";

        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Color_Create")]
        public static extern void ColorCreate(double red, double green, double blue, double opacity, IntPtr outBufferPtr);

        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Color_Destroy")]
        public static extern void ColorDestroy(IntPtr bufferPtr);
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
