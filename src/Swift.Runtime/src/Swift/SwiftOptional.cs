// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

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
public class SwiftOptional<T> : ISwiftObject, IDisposable
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata().Size;

    private SwiftSafeHandle<SwiftOptional<T>> _payload;

    /// <summary>
    /// Gets the safe handle to the underlying Swift payload
    /// </summary>
    public SwiftSafeHandle<SwiftOptional<T>> Payload => _payload;

    /// <summary>
    /// Gets a PayloadBuffer for use in PInvoke calls
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer => new PayloadBuffer<IntPtr>(_payload);

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
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftOptional<T>>(bufferPtr);
    }

    /// <summary>
    /// Returns the TypeMetadata for this object
    /// </summary>
    /// <returns>The TypeMetadata for this object</returns>
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
        // TODO: https://github.com/dotnet/runtimelab/issues/2963
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
            // The additional byte is a discriminator for the enum case
            // https://github.com/swiftlang/swift/blob/8c8ed346edac36f07ece5518f40e35c05e4aa13a/stdlib/public/core/Optional.swift#L121
            Span<byte> payloadSpan = new Span<byte>(payload, (int)metadata.Size - 1);
            SwiftMarshal.MarshalToSwift(value, ref payloadSpan);
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
            var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
            byte* payload = (byte*)instance._payload.DangerousGetHandle();
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
    /// Gets the case of the optional type
    /// </summary>
    public unsafe SwiftOptionalCases Case
    {
        get
        {
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
                byte* payload = (byte*)_payload.DangerousGetHandle();
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
    /// Gets the value of the optional type if the case is Some
    /// </summary>
    public unsafe T Some
    {
        get
        {
            if (Case != SwiftOptionalCases.Some)
            {
                throw new InvalidOperationException("Cannot get Some when case is None");
            }
            var metadata = SwiftObjectHelper<SwiftOptional<T>>.GetTypeMetadata();
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                // Create a copy of the payload for marshalling
                byte* sourcePayload = (byte*)_payload.DangerousGetHandle();
                Span<byte> payloadCopy = stackalloc byte[(int)_payloadSize];
                new Span<byte>(sourcePayload, (int)_payloadSize).CopyTo(payloadCopy);
                fixed (byte* payloadPtr = payloadCopy)
                {
                    return SwiftMarshal.MarshalFromSwift<T>(new IntPtr(payloadPtr));
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
    public T? Value => Case switch
    {
        SwiftOptionalCases.Some => Some,
        SwiftOptionalCases.None => default(T),
        _ => throw new SwiftRuntimeException($"Unknown case {Case}")
    };

    /// <summary>
    /// Returns true if the case is Some
    /// </summary>
    public bool HasValue => Case == SwiftOptionalCases.Some;

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
        _payload?.Dispose();
    }
}

internal static class PInvokesForSwiftOptional
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSqMa")]
    public static extern TypeMetadata _MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);
}
