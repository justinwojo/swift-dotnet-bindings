// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift <c>ClosedRange&lt;Bound&gt;</c>. Swift's
/// <c>@frozen public struct ClosedRange&lt;Bound: Comparable&gt;</c> is an in-place
/// value-type struct with two fields (<c>lowerBound</c>, <c>upperBound</c>), no
/// out-of-line storage. Layout is <c>(Bound, Bound)</c> — total size
/// <c>2 * sizeof(Bound)</c>. The metadata accessor (<c>$sSNMa</c>) takes a
/// <c>Comparable</c> witness table for <typeparamref name="Bound"/>, resolved via
/// <see cref="ComparableConformanceRegistry"/>.
/// </summary>
/// <typeparam name="Bound">The element type for both range endpoints.</typeparam>
public class SwiftClosedRange<Bound> : ISwiftObject, ISwiftStruct, IDisposable
{
    // Lazy initialization mirrors SwiftSet: avoids calling Swift runtime during static
    // construction so non-trivial Bound types (existentials, classes) don't trigger
    // Mono JIT/async assertions in the .cctor.
    private static TypeMetadata? _cachedBoundMetadata;
    private static nuint? _cachedBoundStride;

    private static TypeMetadata CachedBoundTypeMetadata
    {
        get
        {
            _cachedBoundMetadata ??= TypeMetadata.GetTypeMetadataOrThrow<Bound>();
            return _cachedBoundMetadata.Value;
        }
    }

    // Stride (not Size) is the inter-field spacing for a homogeneous (Bound, Bound)
    // layout — Swift rounds each field up to alignment. For types where size != stride
    // (e.g. a struct {Int8; Int64} → size=9, stride=16) the second field sits at +stride,
    // not +size. For well-aligned primitives size == stride so this is invisible there.
    private static nuint BoundStride
    {
        get
        {
            _cachedBoundStride ??= CachedBoundTypeMetadata.Stride;
            return _cachedBoundStride.Value;
        }
    }

    private SwiftSafeHandle<SwiftClosedRange<Bound>> _payload;
    private bool _disposed;

    /// <summary>
    /// Gets the safe handle wrapping the heap-allocated ClosedRange payload buffer.
    /// </summary>
    public SwiftSafeHandle<SwiftClosedRange<Bound>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    /// <summary>
    /// Gets a PayloadBuffer for the indirect-argument @_cdecl bridge path. Returns a
    /// handle-typed buffer because ClosedRange has no fixed 8-byte representation —
    /// generated bindings consume <c>Payload.DangerousGetHandle()</c> directly.
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<IntPtr>(_payload); }
    }

    static SwiftClosedRange()
    {
        // On NativeAOT, eagerly populate the type metadata cache during type init so
        // the explicit-interface GetTypeMetadata is reachable without reflection (parity
        // with SwiftArray/SwiftSet). Mono skips this — calling Swift runtime during static
        // construction can trigger JIT assertions for non-trivial Bound types.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            TryEagerInitialize();
        }
    }

    /// <summary>
    /// Attempts eager initialization of metadata and factory registration for NativeAOT.
    /// Mirrors <see cref="SwiftArray{Element}.TryEagerInitialize"/>. Returns true on
    /// success; false if Bound metadata isn't yet available (e.g., existential bounds —
    /// not a real use case for ClosedRange's Comparable constraint, but the safety net
    /// preserves parity with the rest of the stdlib generics).
    /// </summary>
    internal static bool TryEagerInitialize()
    {
        try
        {
            NativeAotInitialize();
            return true;
        }
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SwiftClosedRange<{typeof(Bound).Name}>: NativeAotInitialize skipped, using lazy init");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotInitialize()
    {
        var _ = SwiftObjectHelper<SwiftClosedRange<Bound>>.GetTypeMetadata();
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        // ClosedRange's metadata accessor requires a Comparable witness table for Bound.
        // Routes through ComparableConformanceRegistry (NativeAOT-safe direct symbol
        // lookup for known scalars; ProtocolWitnessTable.GetOrThrow fallback otherwise).
        var witnessTable = ComparableConformanceRegistry.GetComparableWitnessTable<Bound>();
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftClosedRange<Bound>), _ =>
            SwiftClosedRangePInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, CachedBoundTypeMetadata, witnessTable));
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftClosedRange<Bound>(handle);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftClosedRange<Bound>>.GetTypeMetadata();
        int size = (int)metadata.Size;
        if (size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {size}, Actual: {swiftDestSpan.Length}");
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
                    return size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// Protocol conformance lookup is not used for ClosedRange — the wrapper is consumed
    /// only as a parameter/return value through the standard stdlib-generic cdecl bridge.
    /// </summary>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Constructs a ClosedRange from an existing Swift payload pointer. Used by
    /// <c>NewFromPayload</c>: copies the ClosedRange via VWT InitializeWithCopy so the
    /// new wrapper owns its buffer (so Bound types with RC are retained correctly).
    /// </summary>
    unsafe SwiftClosedRange(IntPtr handle)
    {
        var metadata = SwiftObjectHelper<SwiftClosedRange<Bound>>.GetTypeMetadata();
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftClosedRange<Bound>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new ClosedRange from two endpoints. Equivalent to Swift's
    /// <c>lowerBound...upperBound</c> — caller is responsible for honoring the
    /// Comparable precondition that <paramref name="lowerBound"/> &lt;= <paramref name="upperBound"/>.
    /// (Swift's <c>...</c> operator asserts; this wrapper does not, matching Swift's
    /// <c>init(uncheckedBounds:)</c> contract.)
    /// </summary>
    public unsafe SwiftClosedRange(Bound lowerBound, Bound upperBound)
    {
        var metadata = SwiftObjectHelper<SwiftClosedRange<Bound>>.GetTypeMetadata();
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
        _payload = new SwiftSafeHandle<SwiftClosedRange<Bound>>(bufferPtr);

        int boundStride = (int)BoundStride;
        int boundSize = (int)CachedBoundTypeMetadata.Size;

        // ClosedRange layout is (Bound, Bound) inline — no header. lowerBound sits at
        // offset 0; upperBound sits at +stride (alignment-rounded). For trivially-aligned
        // primitives stride == size, but for padded payloads they differ. Spans use the
        // payload Size for the value-witness write; the inter-field gap uses Stride.
        Span<byte> lowerSpan = new Span<byte>((void*)bufferPtr, boundSize);
        SwiftMarshal.MarshalToSwift(lowerBound, ref lowerSpan);

        Span<byte> upperSpan = new Span<byte>((byte*)bufferPtr + boundStride, boundSize);
        SwiftMarshal.MarshalToSwift(upperBound, ref upperSpan);
    }

    /// <summary>
    /// Gets the lower bound of the range.
    /// </summary>
    public unsafe Bound LowerBound
    {
        get
        {
            ThrowIfDisposed();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                return SwiftMarshal.MarshalFromSwift<Bound>(_payload.DangerousGetHandle());
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Gets the upper bound of the range.
    /// </summary>
    public unsafe Bound UpperBound
    {
        get
        {
            ThrowIfDisposed();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                // upperBound sits at +stride of Bound (alignment-rounded inter-field gap).
                IntPtr upperPtr = _payload.DangerousGetHandle() + (int)BoundStride;
                return SwiftMarshal.MarshalFromSwift<Bound>(upperPtr);
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns a string representation of the range in Swift's <c>lower...upper</c>
    /// shorthand.
    /// </summary>
    public override string ToString()
    {
        ThrowIfDisposed();
        return $"{LowerBound}...{UpperBound}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _payload?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class SwiftClosedRangePInvokes
{
    // Swift.ClosedRange type metadata accessor: $sSNMa
    // Signature mirrors Set's accessor: takes a TypeMetadataRequest, the Bound metadata,
    // and a Comparable witness table for Bound.
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSNMa")]
    public static extern TypeMetadata PInvoke_getMetadata(TypeMetadataRequest request, TypeMetadata boundMetadata, ProtocolWitnessTable comparableWitnessTable);
}
