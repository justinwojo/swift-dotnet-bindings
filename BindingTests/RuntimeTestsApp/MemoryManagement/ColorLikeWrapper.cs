// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Hand-written stand-in for the SwiftUI value wrappers (<c>Color</c>, <c>AnyView</c>, <c>Image</c>,
/// <c>Font</c>, <c>Animation</c>, <c>EdgeInsets</c>): a <b>bare-<see cref="ISwiftObject"/> reference
/// type</b> (a <c>sealed class</c> that does <b>not</b> implement <see cref="ISwiftStruct"/>) whose
/// <c>NewFromPayload</c> <b>adopts</b> the heap buffer into a <see cref="SwiftSafeHandle{T}"/>.
///
/// Those runtime wrappers are hand-written (the generator never emits a bare-<see cref="ISwiftObject"/>
/// reference type for a Swift <i>struct</i>), so BindingTests cannot reach them through generated
/// <c>Optional</c>/<c>Result</c> factories. This mirror reproduces their exact extraction shape using
/// the real <see cref="TrackedRefStruct"/> Swift metadata — a non-POD struct embedding a
/// lifetime-tracked <c>TrackedRef</c> — so the extraction-side ARC balance is observable via
/// <c>LifetimeTracker</c>. Routed through <c>SwiftOptional&lt;ColorLikeWrapper&gt;.NewSome</c> +
/// <c>.Some</c>, it drives <c>MarshalExtractedPayloadValue</c> down the reference-backed-but-not-
/// <see cref="ISwiftStruct"/> path: the copy must take a value-witness <c>+1</c> and the cleanup must
/// recognize the ADOPT shape and leave the adopted buffer alone. The earlier <c>ISwiftStruct</c>-only
/// gate skipped both, freeing the buffer the wrapper adopted (use-after-free / double-free) and
/// under-retaining the shared ref.
/// </summary>
internal sealed class ColorLikeWrapper : ISwiftObject, IDisposable
{
    private SwiftSafeHandle<ColorLikeWrapper> _payload = SwiftSafeHandle<ColorLikeWrapper>.Zero;
    private bool _disposed;

    private ColorLikeWrapper(IntPtr handle)
    {
        // ADOPT: store the buffer pointer directly, exactly like SwiftUI.Color's from-handle ctor.
        _payload = new SwiftSafeHandle<ColorLikeWrapper>(handle);
    }

    /// <summary>
    /// Builds an independently-owned wrapper from a fresh value-witness copy of a
    /// <see cref="TrackedRefStruct"/> payload (a <c>+1</c> on the embedded ref), mirroring how a SwiftUI
    /// wrapper is constructed from a native helper that returns a retained buffer.
    /// </summary>
    public static unsafe ColorLikeWrapper FromTrackedRefStruct(TrackedRefStruct source)
    {
        TypeMetadata metadata = TypeMetadata.GetTypeMetadataOrThrow<TrackedRefStruct>();
        byte* buffer = (byte*)NativeMemory.AllocZeroed(metadata.Size);
        metadata.ValueWitnessTable->InitializeWithCopy(buffer, (void*)((ISwiftObject)source).SwiftHandle, metadata);
        return new ColorLikeWrapper((IntPtr)buffer);
    }

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    static TypeMetadata ISwiftObject.GetTypeMetadata()
        => TypeMetadata.GetTypeMetadataOrThrow<TrackedRefStruct>();

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => new ColorLikeWrapper(handle);

    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        TypeMetadata metadata = TypeMetadata.GetTypeMetadataOrThrow<TrackedRefStruct>();
        if ((int)metadata.Size > swiftDestSpan.Length)
            throw new ArgumentException($"Span size mismatch, expected {(int)metadata.Size}, actual {swiftDestSpan.Length}");

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

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        => throw new SwiftRuntimeException($"Protocol conformance not implemented for ColorLikeWrapper and {typeof(TProtocol).Name}");

    public void Dispose()
    {
        if (!_disposed)
        {
            _payload.Dispose();
            _disposed = true;
        }
    }
}
