// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Swift;
using Swift.Runtime;

namespace SwiftUI;

/// <summary>
/// Represents SwiftUI.Font - a font used for rendering text.
/// </summary>
/// <remarks>
/// SwiftUI.Font predates the runtime's deployment floor on every platform, so the type
/// needs no <see cref="SupportedOSPlatformAttribute"/> version annotation, and the type
/// itself is fully usable on Mac Catalyst — its metadata resolves and the
/// payload-marshalling path binds there. The single Catalyst restriction is
/// <see cref="System(double, Font.Weight, Font.Design)"/>: the SwiftUI construction shims
/// are compiled out of the macabi slice of the runtime library, so that one method throws
/// <see cref="PlatformNotSupportedException"/> there. The attribute is therefore on the
/// factory, not the type.
/// </remarks>
public sealed class Font : ISwiftObject, IDisposable
{
    /// <summary>
    /// Weight of a system font, mirroring <c>SwiftUI.Font.Weight</c>.
    /// </summary>
    /// <remarks>
    /// <c>Font.Weight</c> is an opaque Swift struct with no public raw value, so these
    /// numeric values ARE the ABI contract with the native <c>SBW_SwiftUI_Font_System</c>
    /// shim, which switches on them to pick the matching <c>Font.Weight</c> member. The
    /// order runs lightest to heaviest; a new weight appends a value and never renumbers
    /// an existing one.
    /// </remarks>
    public enum Weight
    {
        /// <summary>Maps to <c>Font.Weight.ultraLight</c>.</summary>
        UltraLight = 0,
        /// <summary>Maps to <c>Font.Weight.thin</c>.</summary>
        Thin = 1,
        /// <summary>Maps to <c>Font.Weight.light</c>.</summary>
        Light = 2,
        /// <summary>Maps to <c>Font.Weight.regular</c>.</summary>
        Regular = 3,
        /// <summary>Maps to <c>Font.Weight.medium</c>.</summary>
        Medium = 4,
        /// <summary>Maps to <c>Font.Weight.semibold</c>.</summary>
        Semibold = 5,
        /// <summary>Maps to <c>Font.Weight.bold</c>.</summary>
        Bold = 6,
        /// <summary>Maps to <c>Font.Weight.heavy</c>.</summary>
        Heavy = 7,
        /// <summary>Maps to <c>Font.Weight.black</c>.</summary>
        Black = 8,
    }

    /// <summary>
    /// Typeface design of a system font, mirroring <c>SwiftUI.Font.Design</c>.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="Weight"/>: <c>Font.Design</c> is not raw-representable,
    /// so these numeric values are the ABI shared with the native shim.
    /// </remarks>
    public enum Design
    {
        /// <summary>Maps to <c>Font.Design.default</c>.</summary>
        Default = 0,
        /// <summary>Maps to <c>Font.Design.serif</c>.</summary>
        Serif = 1,
        /// <summary>Maps to <c>Font.Design.rounded</c>.</summary>
        Rounded = 2,
        /// <summary>Maps to <c>Font.Design.monospaced</c>.</summary>
        Monospaced = 3,
    }

    private SwiftSafeHandle<Font> _payload = SwiftSafeHandle<Font>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<Font> Payload => _payload;

    /// <summary>
    /// Blittable stand-in for the frozen layout of <c>SwiftUI.Font</c>: a single 8-byte
    /// reference to its refcounted font-provider box.
    /// </summary>
    /// <remarks>
    /// Managed code never reads the field — only size and alignment matter. Swift passes a
    /// frozen <c>Font</c> directly in a register rather than through a pointer, so bindings
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
    public unsafe PayloadBuffer<Font.Buffer> PayloadBuffer => new PayloadBuffer<Font.Buffer>(_payload);

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
        return new Font(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for Font and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Construction

    internal Font(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<Font>(handle);
    }

    /// <summary>
    /// Creates a system font of the given point size, weight and typeface design.
    /// </summary>
    /// <param name="size">Point size. Must be finite and greater than zero.</param>
    /// <param name="weight">Font weight; defaults to <see cref="Weight.Regular"/>.</param>
    /// <param name="design">Typeface design; defaults to <see cref="Design.Default"/>.</param>
    /// <returns>A new <see cref="Font"/> owning its Swift payload; dispose it when done.</returns>
    /// <remarks>
    /// Maps onto SwiftUI's <c>Font.system(size:weight:design:)</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="size"/> is not a finite positive number, or <paramref name="weight"/>
    /// / <paramref name="design"/> is outside the declared enum range.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">Running on Mac Catalyst.</exception>
    [UnsupportedOSPlatform("maccatalyst")]
    public static unsafe Font System(double size, Weight weight = Weight.Regular, Design design = Design.Default)
    {
        if (OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("SwiftUI.Font construction is not available on Mac Catalyst (SBW_SwiftUI_Font_System is not exported in the macabi runtime slice).");

        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be a finite positive number.");

        // Range-checked here rather than in the shim: an out-of-contract code would silently
        // resolve to regular/default on the Swift side, which hides the caller's mistake.
        if (weight < Weight.UltraLight || weight > Weight.Black)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Unknown font weight.");
        if (design < Design.Default || design > Design.Monospaced)
            throw new ArgumentOutOfRangeException(nameof(design), design, "Unknown font design.");

        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();

        // NativeMemory rather than Marshal.AllocHGlobal: on the success path the buffer is
        // handed to SwiftSafeHandle, which always releases it with NativeMemory.Free, so the
        // allocation has to come from the matching allocator.
        IntPtr handle = (IntPtr)NativeMemory.Alloc((nuint)metadata.Size);

        // The Swift value is live in the buffer the moment the shim returns, so anything
        // that throws after that point owes it a VWT-equivalent destroy before the buffer
        // is freed — Font holds a refcounted font provider.
        bool initialized = false;
        try
        {
            NativeMethods.FontSystem(size, (int)weight, (int)design, handle);
            initialized = true;
            return new Font(handle);
        }
        catch
        {
            if (initialized)
                NativeMethods.FontDestroy(handle);
            NativeMemory.Free((void*)handle);
            throw;
        }
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftUI, EntryPoint = "$s7SwiftUI4FontVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

    private static class NativeMethods
    {
        private const string RuntimeLib = "SwiftBindingsRuntime";

        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Font_System")]
        public static extern void FontSystem(double size, int weightCode, int designCode, IntPtr outBufferPtr);

        [DllImport(RuntimeLib, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SBW_SwiftUI_Font_Destroy")]
        public static extern void FontDestroy(IntPtr bufferPtr);
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
