// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Defines the possible cases for a Result type
/// </summary>
public enum SwiftResultCase : uint
{
    Success,
    Failure,
}

/// <summary>
/// Represents a Swift Result type.
/// Swift.Result is a frozen enum with two cases: success and failure.
/// TSuccess is the type of the success value, TFailure is the type of the failure value
/// (typically conforming to Swift.Error).
/// </summary>
/// <remarks>
/// This is a minimal implementation to support closure parameters that receive Result types.
/// Full case discrimination and value extraction requires understanding Swift enum memory layout
/// and will be implemented in future work.
/// </remarks>
public class SwiftResult<TSuccess, TFailure> : ISwiftObject, IDisposable
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata().Size;

    private SwiftSafeHandle<SwiftResult<TSuccess, TFailure>> _payload;

    /// <summary>
    /// Gets the safe handle to the underlying Swift payload
    /// </summary>
    public SwiftSafeHandle<SwiftResult<TSuccess, TFailure>> Payload => _payload;

    /// <summary>
    /// Gets a PayloadBuffer for use in PInvoke calls
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer => new PayloadBuffer<IntPtr>(_payload);

    /// <summary>
    /// Constructs a new empty SwiftResult with allocated native memory
    /// </summary>
    unsafe SwiftResult()
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.AllocZeroed(_payloadSize);
        _payload = new SwiftSafeHandle<SwiftResult<TSuccess, TFailure>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftResult from the given handle
    /// </summary>
    unsafe SwiftResult(IntPtr handle)
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(_payloadSize);
        var metadata = SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata();
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftResult<TSuccess, TFailure>>(bufferPtr);
    }

    /// <summary>
    /// Returns the TypeMetadata for this object
    /// </summary>
    /// <returns>The TypeMetadata for this object</returns>
    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftResult<TSuccess, TFailure>), _ =>
                PInvokesForSwiftResult._MetadataAccessor(
                    TypeMetadataRequest.Complete,
                    TypeMetadata.GetTypeMetadataOrThrow<TSuccess>(),
                    TypeMetadata.GetTypeMetadataOrThrow<TFailure>()));
    }

    /// <summary>
    /// Creates a new SwiftResult from a Swift payload
    /// </summary>
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        return new SwiftResult<TSuccess, TFailure>(payload);
    }

    /// <summary>
    /// Marshals this object to a Swift destination
    /// </summary>
    /// <param name="swiftDestSpan"></param>
    /// <returns></returns>
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata();
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
        // TODO: Implement protocol conformance for Result
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the case of the result type
    /// </summary>
    /// <remarks>
    /// Note: This is a stub implementation. Full enum case detection requires understanding
    /// Swift's enum layout which varies based on the payload types.
    /// </remarks>
    public unsafe SwiftResultCase Case
    {
        get
        {
            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var metadata = SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata();
                byte* payload = (byte*)_payload.DangerousGetHandle();
                return (SwiftResultCase)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns true if the result is a success case
    /// </summary>
    public bool IsSuccess => Case == SwiftResultCase.Success;

    /// <summary>
    /// Returns true if the result is a failure case
    /// </summary>
    public bool IsFailure => Case == SwiftResultCase.Failure;

    /// <summary>
    /// Releases the resources used by the SwiftResult.
    /// </summary>
    public void Dispose()
    {
        _payload?.Dispose();
    }
}

internal static class PInvokesForSwiftResult
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss6ResultOMa")]
    public static extern TypeMetadata _MetadataAccessor(TypeMetadataRequest request, TypeMetadata successMetadata, TypeMetadata failureMetadata);
}
