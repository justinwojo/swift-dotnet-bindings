// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace Swift;

/// <summary>
/// Defines the possible cases for an optional type
/// </summary>
public enum SwiftOptionalCases : uint
{
    Some,
    None,
}

/// <summary>
/// Represents a Swift Optional type
/// </summary>
public class SwiftOptional<T> : ISwiftObject, ISwiftStruct, IDisposable
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata().Size;

    /// <summary>
    /// Returns the tag byte offset for the Optional discriminator, or -1 if the type uses
    /// extra inhabitants (no separate tag byte).
    ///
    /// When Optional&lt;T&gt;.Size &gt; T.Size, the Optional uses an appended tag byte at offset T.Size.
    /// Tag byte values: 0 = Some, 1 = None.
    ///
    /// When Optional&lt;T&gt;.Size == T.Size, the type has extra inhabitants (e.g., classes where nil
    /// pointer encodes None) and the VWT must be used.
    ///
    /// This generalizes the blittable primitive fast path from Session 8 to cover all types
    /// without extra inhabitants, including complex enums and non-frozen structs.
    /// </summary>
    internal static int GetTagByteOffset()
    {
        // Blittable primitive fast path (original Session 8 logic) — these are known at compile time
        // and avoid the metadata lookup cost.
        var blittableOffset = GetBlittablePrimitiveTagOffset();
        if (blittableOffset >= 0)
            return blittableOffset;

        // General case: compare Optional<T>.Size vs T.Size.
        // If Optional is larger, the tag byte is at offset T.Size.
        var optionalSize = (int)_payloadSize;
        var innerSize = (int)TypeMetadata.GetTypeMetadataOrThrow<T>().Size;
        if (optionalSize > innerSize)
            return innerSize;

        // Extra-inhabitant type (class, string, etc.) — no tag byte, must use VWT.
        return -1;
    }

    private SwiftSafeHandle<SwiftOptional<T>> _payload;
    private bool _disposed;

    /// <summary>
    /// Gets the safe handle to the underlying Swift payload
    /// </summary>
    public SwiftSafeHandle<SwiftOptional<T>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    /// <summary>
    /// Gets a PayloadBuffer for use in PInvoke calls
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get
        {
            ThrowIfDisposed();
            Debug.Assert(_payloadSize <= (nuint)IntPtr.Size,
                $"SwiftOptional<{typeof(T).Name}> payload size ({_payloadSize}) exceeds IntPtr size ({IntPtr.Size}). " +
                "Use DangerousGetHandle() instead of PayloadBuffer for large Optional types.");
            return new PayloadBuffer<IntPtr>(_payload);
        }
    }

    /// <summary>
    /// Constructs a new empty SwiftOptional with allocated native memory
    /// </summary>
    unsafe SwiftOptional()
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.AllocZeroed(_payloadSize);
        _payload = new SwiftSafeHandle<SwiftOptional<T>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftOptional from the given handle
    /// </summary>
    unsafe SwiftOptional(IntPtr handle)
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(_payloadSize);
        var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
        var tagOffset = GetTagByteOffset();
        if (tagOffset >= 0 && !metadata.ValueWitnessTable->IsNonPOD)
        {
            // POD tag-byte fast path: for trivially-copyable types with an appended tag byte
            // (e.g., Optional<CGPoint>, Optional<CGRect>), use direct memcpy instead of VWT
            // InitializeWithCopy. The VWT for some Clang-imported struct Optionals copies
            // only the payload bytes without the tag byte, causing None to be read as Some.
            // Gate: IsNonPOD=false ensures the payload has no retained references that need
            // ref-counting on copy. Non-POD payloads with tag bytes (e.g., frozen structs
            // containing class references) must still use InitializeWithCopy.
            Buffer.MemoryCopy((void*)handle, (void*)bufferPtr, (long)_payloadSize, (long)_payloadSize);
        }
        else
        {
            // VWT path: handles extra-inhabitant types (classes, strings), non-POD payloads
            // with retained references, and any case where memcpy isn't safe.
            metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        }
        _payload = new SwiftSafeHandle<SwiftOptional<T>>(bufferPtr);
    }

    /// <summary>
    /// Returns the TypeMetadata for this object
    /// </summary>
    /// <returns>The TypeMetadata for this object</returns>
    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftOptional<T>), _ =>
                PInvokesForSwiftOptional._MetadataAccessor(TypeMetadataRequest.Complete, TypeMetadata.GetTypeMetadataOrThrow<T>()));
    }

    /// <summary>
    /// Creates a new SwiftOptional from a Swift payload
    /// </summary>
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        return new SwiftOptional<T>(payload);
    }

    /// <summary>
    /// Marshals this object to a Swift destination
    /// </summary>
    /// <param name="swiftDestSpan"></param>
    /// <returns></returns>
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure the payload is valid before making copy
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

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        // Protocol conformance for SwiftOptional is not implemented.
        // SwiftOptional wraps Swift's Optional<T> but doesn't support protocol witness lookup.
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates a new SwiftOptional with a Some case payload
    /// </summary>
    public static unsafe SwiftOptional<T> NewSome(T value)
    {
        var instance = new SwiftOptional<T>();
        bool success = false;
        instance._payload.DangerousAddRef(ref success);
        try
        {
            var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
            byte* payload = (byte*)instance._payload.DangerousGetHandle();
            var innerSize = (int)TypeMetadata.GetTypeMetadataOrThrow<T>().Size;
            int spanSize = ComputePayloadSpanSize((int)metadata.Size, innerSize);
            Span<byte> payloadSpan = new Span<byte>(payload, spanSize);
            SwiftMarshal.MarshalToSwift(value, ref payloadSpan);

            // Tag byte fast path: for types without extra inhabitants (optionalSize > innerSize),
            // the tag byte at offset innerSize is already 0 (Some) from AllocZeroed.
            // Skip DestructiveInjectEnumTag which produces incorrect results on some runtimes
            // (Mono iOS Simulator) for Optional<Int32>, Optional<ComplexEnum>, etc.
            var tagOffset = GetTagByteOffset();
            if (tagOffset >= 0)
            {
                // Tag byte is already 0 (Some) from AllocZeroed — no action needed.
                return instance;
            }

            metadata.ValueWitnessTable->DestructiveInjectEnumTag(payload, (uint)SwiftOptionalCases.Some, metadata);
            return instance;
        }
        finally
        {
            if (success)
                instance._payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Creates a new SwiftOptional with no payload
    /// </summary>
    public static unsafe SwiftOptional<T> NewNone()
    {
        var instance = new SwiftOptional<T>();
        bool success = false;
        instance._payload.DangerousAddRef(ref success);
        try
        {
            byte* payload = (byte*)instance._payload.DangerousGetHandle();

            // Tag byte fast path: for types without extra inhabitants (optionalSize > innerSize),
            // write the None tag byte (1) directly at offset innerSize instead of using VWT
            // DestructiveInjectEnumTag, which produces incorrect results on some runtimes
            // (Mono iOS Simulator) for Optional<Int32>, Optional<ComplexEnum>, etc.
            var tagOffset = GetTagByteOffset();
            if (tagOffset >= 0)
            {
                payload[tagOffset] = 1; // None
                return instance;
            }

            var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
            metadata.ValueWitnessTable->DestructiveInjectEnumTag(payload, (uint)SwiftOptionalCases.None, metadata);
            return instance;
        }
        finally
        {
            if (success)
                instance._payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Computes the payload span size for NewSome marshalling.
    /// Uses the inner type's size (not Optional's size minus one) to handle
    /// extra-inhabitant types (String, Array, classes) where Optional&lt;T&gt;.Size == T.Size.
    /// </summary>
    /// <param name="optionalSize">Size of Optional&lt;T&gt; from metadata.</param>
    /// <param name="innerSize">Size of T from metadata.</param>
    /// <returns>Number of bytes to marshal for the payload.</returns>
    internal static int ComputePayloadSpanSize(int optionalSize, int innerSize)
    {
        Debug.Assert(innerSize > 0, "Inner type size must be positive");
        Debug.Assert(innerSize <= optionalSize,
            $"Inner type size ({innerSize}) exceeds Optional size ({optionalSize})");
        return innerSize;
    }

    /// <summary>
    /// Gets the case of the optional type
    /// </summary>
    public unsafe SwiftOptionalCases Case
    {
        get
        {
            ThrowIfDisposed();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                byte* payload = (byte*)_payload.DangerousGetHandle();

                // Tag byte fast path: for types without extra inhabitants (optionalSize > innerSize),
                // read the tag byte directly at offset innerSize instead of going through VWT
                // GetEnumTag, which returns incorrect values on some runtimes (Mono on iOS Simulator).
                // Layout: [innerSize bytes payload][1 byte tag: 0=Some, 1=None]
                var tagOffset = GetTagByteOffset();
                if (tagOffset >= 0)
                {
                    return payload[tagOffset] == 0 ? SwiftOptionalCases.Some : SwiftOptionalCases.None;
                }

                var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
                return (SwiftOptionalCases)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns the tag byte offset for blittable primitive types, or -1 if not applicable.
    /// For Optional&lt;Int32&gt;, the tag byte is at offset 4 (sizeof(Int32)).
    /// Note: Bool is excluded — Optional&lt;Bool&gt; uses extra inhabitants (size 1 == size 1),
    /// not an appended tag byte. Bool falls through to GetTagByteOffset()'s size comparison.
    /// </summary>
    private static int GetBlittablePrimitiveTagOffset()
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
            return 1;
        if (typeof(T) == typeof(short) || typeof(T) == typeof(ushort))
            return 2;
        if (typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(float))
            return 4;
        if (typeof(T) == typeof(long) || typeof(T) == typeof(ulong) || typeof(T) == typeof(double))
            return 8;
        if (typeof(T) == typeof(nint) || typeof(T) == typeof(nuint))
            return IntPtr.Size;
        return -1;
    }

    /// <summary>
    /// Gets the value of the optional type if the case is Some
    /// </summary>
    public unsafe T Some
    {
        get
        {
            ThrowIfDisposed();
            if (Case != SwiftOptionalCases.Some)
            {
                throw new InvalidOperationException("Cannot get Some when case is None");
            }
            var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                byte* sourcePayload = (byte*)_payload.DangerousGetHandle();

                // For true Swift class types (SwiftClassHandle), the payload IS
                // the class pointer (8 bytes). MarshalFromSwift/NewFromPayload for class
                // types expects the pointer value directly, not a pointer to memory
                // containing it. Read the class pointer and pass it directly.
                //
                // Buffer-backed types (SwiftSafeHandle) — including C# classes wrapping
                // Swift structs like SwiftString, URL, SwiftArray — implement ISwiftStruct
                // and must use the heap-copy path below, because their NewFromPayload
                // expects a pointer to memory containing the struct's raw bytes.
                if (typeof(ISwiftObject).IsAssignableFrom(typeof(T))
                    && !typeof(T).IsValueType
                    && !typeof(ISwiftStruct).IsAssignableFrom(typeof(T)))
                {
                    IntPtr classPointer = *(IntPtr*)sourcePayload;
                    return SwiftMarshal.MarshalFromSwift<T>(classPointer);
                }

                // For value types, ISwiftStruct types, and non-ISwiftObject types: heap-copy the payload
                // bytes. We can't use stackalloc because ISwiftObject.NewFromPayload
                // takes ownership of the pointer (stores it in SwiftSafeHandle which
                // calls NativeMemory.Free).
                byte* heapCopy = (byte*)NativeMemory.Alloc(_payloadSize);
                new Span<byte>(sourcePayload, (int)_payloadSize).CopyTo(
                    new Span<byte>(heapCopy, (int)_payloadSize));
                try
                {
                    return SwiftMarshal.MarshalFromSwift<T>(new IntPtr(heapCopy));
                }
                finally
                {
                    // ISwiftObject.NewFromPayload takes ownership of the buffer,
                    // so only free for non-ISwiftObject types (primitives, tuples, etc.)
                    if (!typeof(ISwiftObject).IsAssignableFrom(typeof(T)))
                    {
                        NativeMemory.Free(heapCopy);
                    }
                }
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Gets the value of the optional type if the case is Some or the default value if the case is None
    /// </summary>
    public T? Value
    {
        get
        {
            ThrowIfDisposed();
            return Case switch
    {
        SwiftOptionalCases.Some => Some,
        SwiftOptionalCases.None => default(T),
            _ => throw new SwiftRuntimeException($"Unknown case {Case}")
            };
        }
    }

    /// <summary>
    /// Returns true if the case is Some
    /// </summary>
    public bool HasValue
    {
        get { ThrowIfDisposed(); return Case == SwiftOptionalCases.Some; }
    }

    /// <summary>
    /// Creates a SwiftOptional from a nullable value.
    /// Returns None if the value is null, otherwise returns Some with the value.
    /// </summary>
    /// <param name="value">The nullable value to convert.</param>
    /// <returns>A SwiftOptional representing the nullable value.</returns>
    public static SwiftOptional<T> FromNullable(T? value)
    {
        if (value == null)
            return NewNone();
        return NewSome(value);
    }

    /// <summary>
    /// Converts the SwiftOptional to a nullable value.
    /// Returns null if the case is None, otherwise returns the unwrapped value.
    /// </summary>
    /// <returns>The nullable value representation.</returns>
    public T? ToNullable()
    {
        ThrowIfDisposed();
        return Case == SwiftOptionalCases.Some ? Some : default;
    }

    /// <summary>
    /// Implicitly converts a SwiftOptional to a nullable value.
    /// </summary>
    public static implicit operator T?(SwiftOptional<T> optional)
    {
        if (optional == null)
            return default;
        return optional.ToNullable();
    }

    /// <summary>
    /// Implicitly converts a nullable value to a SwiftOptional.
    /// </summary>
    public static implicit operator SwiftOptional<T>(T? value)
    {
        return FromNullable(value);
    }

    /// <summary>
    /// Releases the resources used by the SwiftOptional.
    /// </summary>
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

internal static class PInvokesForSwiftOptional
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSqMa")]
    public static extern TypeMetadata _MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);
}
