// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;

namespace SwiftUI;

/// <summary>
/// Represents SwiftUI.Animation - an animation that can be applied to a view.
/// </summary>
public sealed class Animation : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<Animation> _payload = SwiftSafeHandle<Animation>.Zero;
    private bool _disposed;

    private static TypeMetadata? _cachedMetadata;

    /// <summary>
    /// Gets the internal handle for marshalling to Swift.
    /// </summary>
    public SwiftSafeHandle<Animation> Payload => _payload;

    /// <summary>
    /// Blittable stand-in for the frozen layout of <c>SwiftUI.Animation</c>: a single 8-byte
    /// reference to its refcounted animation box.
    /// </summary>
    /// <remarks>
    /// Managed code never reads the field — only size and alignment matter. Swift passes a
    /// frozen <c>Animation</c> directly in a register rather than through a pointer, so bindings
    /// that take one as a parameter pass this struct by value.
    /// </remarks>
    public struct Buffer
    {
#pragma warning disable CS0169
        private IntPtr _animationBox;
#pragma warning restore CS0169
    }

    /// <summary>
    /// Pins the payload for the duration of a call and exposes it as the by-value
    /// <see cref="Buffer"/> the Swift ABI expects. Dispose to release the pin.
    /// </summary>
    public unsafe PayloadBuffer<Animation.Buffer> PayloadBuffer => new PayloadBuffer<Animation.Buffer>(_payload);

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
        return new Animation(handle);
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
        throw new SwiftRuntimeException($"Protocol conformance not implemented for Animation and {typeof(TProtocol).Name}");
    }

    #endregion

    #region Private Constructor

    internal Animation(IntPtr handle)
    {
        _payload = new SwiftSafeHandle<Animation>(handle);
    }

    #endregion

    #region P/Invoke Declarations

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftUI, EntryPoint = "$s7SwiftUI9AnimationVMa")]
    private static extern TypeMetadata PInvoke_GetMetadata();

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
