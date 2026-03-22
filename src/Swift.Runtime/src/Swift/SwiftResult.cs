// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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
public class SwiftResult<TSuccess, TFailure> : ISwiftObject, ISwiftStruct, IDisposable
{
    static nuint _payloadSize = SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata().Size;

    private SwiftSafeHandle<SwiftResult<TSuccess, TFailure>> _payload;
    private bool _disposed;

    /// <summary>
    /// Gets the safe handle to the underlying Swift payload
    /// </summary>
    public SwiftSafeHandle<SwiftResult<TSuccess, TFailure>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    /// <summary>
    /// Gets a PayloadBuffer for use in PInvoke calls
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<IntPtr>(_payload); }
    }

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
    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

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
        ThrowIfDisposed();
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
            ThrowIfDisposed();
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
    public bool IsSuccess
    {
        get { ThrowIfDisposed(); return Case == SwiftResultCase.Success; }
    }

    /// <summary>
    /// Returns true if the result is a failure case
    /// </summary>
    public bool IsFailure
    {
        get { ThrowIfDisposed(); return Case == SwiftResultCase.Failure; }
    }

    /// <summary>
    /// Gets the success value. Throws if the result is a failure.
    /// </summary>
    public unsafe TSuccess Success
    {
        get
        {
            ThrowIfDisposed();
            if (Case != SwiftResultCase.Success)
                throw new InvalidOperationException("Cannot get Success when case is Failure");

            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                byte* sourcePayload = (byte*)_payload.DangerousGetHandle();
                Span<byte> payloadCopy = stackalloc byte[(int)_payloadSize];
                new Span<byte>(sourcePayload, (int)_payloadSize).CopyTo(payloadCopy);
                fixed (byte* payloadPtr = payloadCopy)
                {
                    return SwiftMarshal.MarshalFromSwift<TSuccess>(new IntPtr(payloadPtr));
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
    /// Gets the failure value. Throws if the result is a success.
    /// </summary>
    public unsafe TFailure Failure
    {
        get
        {
            ThrowIfDisposed();
            if (Case != SwiftResultCase.Failure)
                throw new InvalidOperationException("Cannot get Failure when case is Success");

            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                byte* sourcePayload = (byte*)_payload.DangerousGetHandle();
                Span<byte> payloadCopy = stackalloc byte[(int)_payloadSize];
                new Span<byte>(sourcePayload, (int)_payloadSize).CopyTo(payloadCopy);
                fixed (byte* payloadPtr = payloadCopy)
                {
                    return SwiftMarshal.MarshalFromSwift<TFailure>(new IntPtr(payloadPtr));
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
    /// Attempts to get the success value.
    /// </summary>
    /// <param name="value">When this method returns, contains the success value if the result is a success case; otherwise, the default value.</param>
    /// <returns><c>true</c> if the result is a success case; otherwise, <c>false</c>.</returns>
    public bool TryGetSuccess([MaybeNullWhen(false)] out TSuccess value)
    {
        ThrowIfDisposed();
        if (Case == SwiftResultCase.Success)
        {
            value = Success;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Attempts to get the failure value.
    /// </summary>
    /// <param name="value">When this method returns, contains the failure value if the result is a failure case; otherwise, the default value.</param>
    /// <returns><c>true</c> if the result is a failure case; otherwise, <c>false</c>.</returns>
    public bool TryGetFailure([MaybeNullWhen(false)] out TFailure value)
    {
        ThrowIfDisposed();
        if (Case == SwiftResultCase.Failure)
        {
            value = Failure;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Pattern matching helper that calls the appropriate handler based on the result case.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the handlers.</typeparam>
    /// <param name="onSuccess">The handler to call if the result is a success.</param>
    /// <param name="onFailure">The handler to call if the result is a failure.</param>
    /// <returns>The result from the appropriate handler.</returns>
    public TResult Match<TResult>(Func<TSuccess, TResult> onSuccess, Func<TFailure, TResult> onFailure)
    {
        ThrowIfDisposed();
        return Case switch
        {
            SwiftResultCase.Success => onSuccess(Success),
            SwiftResultCase.Failure => onFailure(Failure),
            _ => throw new InvalidOperationException($"Unknown case {Case}")
        };
    }

    /// <summary>
    /// Creates a success result with the given value.
    /// </summary>
    /// <param name="value">The success value.</param>
    /// <returns>A new SwiftResult in the success case.</returns>
    public static SwiftResult<TSuccess, TFailure> FromSuccess(TSuccess value)
    {
        return new SuccessResult(value);
    }

    /// <summary>
    /// Creates a failure result with the given error.
    /// </summary>
    /// <param name="error">The failure value.</param>
    /// <returns>A new SwiftResult in the failure case.</returns>
    public static SwiftResult<TSuccess, TFailure> FromFailure(TFailure error)
    {
        return new FailureResult(error);
    }

    /// <summary>
    /// Releases the resources used by the SwiftResult.
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

    /// <summary>
    /// Internal class representing a success result that doesn't require Swift interop.
    /// Used when creating results from C# code (e.g., for throwing closure callbacks).
    /// </summary>
    private sealed class SuccessResult : SwiftResult<TSuccess, TFailure>
    {
        private readonly TSuccess _value;

        public SuccessResult(TSuccess value) : base()
        {
            _value = value;
        }

        public new SwiftResultCase Case => SwiftResultCase.Success;
        public new bool IsSuccess => true;
        public new bool IsFailure => false;
        public new TSuccess Success => _value;
        public new TFailure Failure => throw new InvalidOperationException("Cannot get Failure when case is Success");
    }

    /// <summary>
    /// Internal class representing a failure result that doesn't require Swift interop.
    /// Used when creating results from C# code (e.g., for throwing closure callbacks).
    /// </summary>
    private sealed class FailureResult : SwiftResult<TSuccess, TFailure>
    {
        private readonly TFailure _error;

        public FailureResult(TFailure error) : base()
        {
            _error = error;
        }

        public new SwiftResultCase Case => SwiftResultCase.Failure;
        public new bool IsSuccess => false;
        public new bool IsFailure => true;
        public new TSuccess Success => throw new InvalidOperationException("Cannot get Success when case is Failure");
        public new TFailure Failure => _error;
    }
}

internal static class PInvokesForSwiftResult
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss6ResultOMa")]
    public static extern TypeMetadata _MetadataAccessor(TypeMetadataRequest request, TypeMetadata successMetadata, TypeMetadata failureMetadata);
}
