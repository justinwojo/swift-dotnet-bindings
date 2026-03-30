// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
    // Lazy to avoid triggering Swift metadata resolution when the type is first accessed.
    // C#-only subclasses (SuccessResult, FailureResult) never need Swift metadata.
    static readonly Lazy<nuint> _payloadSizeLazy = new(() =>
        SwiftObjectHelper<SwiftResult<TSuccess, TFailure>>.GetTypeMetadata().Size);
    static nuint PayloadSize => _payloadSizeLazy.Value;

    private SwiftSafeHandle<SwiftResult<TSuccess, TFailure>>? _payload;
    private bool _disposed;

    private static readonly Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftResult()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftHashable), "$ss6ResultOyxq_GSHsSHRzSHR_rlMc" },
        };

        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            NativeAotRegisterConformances();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotRegisterConformances()
    {
        foreach (var (protocolType, symbol) in _protocolConformanceSymbols)
        {
            var symbolName = symbol;
            ConformanceDispatcher.Register(typeof(SwiftResult<TSuccess, TFailure>), protocolType,
                () => ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName));
        }
    }

    /// <summary>
    /// Gets the safe handle to the underlying Swift payload
    /// </summary>
    public SwiftSafeHandle<SwiftResult<TSuccess, TFailure>> Payload
    {
        get { ThrowIfDisposed(); ThrowIfCSharpOnly(); return _payload!; }
    }

    /// <summary>
    /// Gets a PayloadBuffer for use in PInvoke calls
    /// </summary>
    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); ThrowIfCSharpOnly(); return new PayloadBuffer<IntPtr>(_payload!); }
    }

    /// <summary>
    /// Constructs a C#-only SwiftResult without allocating native memory.
    /// Used by SuccessResult/FailureResult which store values in C# fields
    /// and never need Swift interop.
    /// </summary>
    private SwiftResult(bool csharpOnly)
    {
        // No native allocation — _payload stays null.
        // Virtual property overrides in subclasses handle all access.
    }

    /// <summary>
    /// Constructs a new empty SwiftResult with allocated native memory
    /// </summary>
    unsafe SwiftResult()
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.AllocZeroed(PayloadSize);
        _payload = new SwiftSafeHandle<SwiftResult<TSuccess, TFailure>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftResult from the given handle
    /// </summary>
    unsafe SwiftResult(IntPtr handle)
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(PayloadSize);
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
        get { ThrowIfDisposed(); ThrowIfCSharpOnly(); return _payload!.DangerousGetHandle(); }
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
        ThrowIfCSharpOnly();
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
                _payload!.DangerousAddRef(ref success);
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
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
        {
            throw new SwiftRuntimeException(
                $"Attempted to retrieve protocol conformance descriptor for type SwiftResult and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Gets the case of the result type
    /// </summary>
    /// <remarks>
    /// Note: This is a stub implementation. Full enum case detection requires understanding
    /// Swift's enum layout which varies based on the payload types.
    /// </remarks>
    public virtual unsafe SwiftResultCase Case
    {
        get
        {
            ThrowIfDisposed();
            bool success = false;
            _payload!.DangerousAddRef(ref success);
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
    public virtual bool IsSuccess
    {
        get { ThrowIfDisposed(); return Case == SwiftResultCase.Success; }
    }

    /// <summary>
    /// Returns true if the result is a failure case
    /// </summary>
    public virtual bool IsFailure
    {
        get { ThrowIfDisposed(); return Case == SwiftResultCase.Failure; }
    }

    /// <summary>
    /// Gets the success value. Throws if the result is a failure.
    /// </summary>
    public virtual unsafe TSuccess Success
    {
        get
        {
            ThrowIfDisposed();
            if (Case != SwiftResultCase.Success)
                throw new InvalidOperationException("Cannot get Success when case is Failure");

            bool success = false;
            _payload!.DangerousAddRef(ref success);
            try
            {
                return ExtractPayloadValue<TSuccess>();
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
    public virtual unsafe TFailure Failure
    {
        get
        {
            ThrowIfDisposed();
            if (Case != SwiftResultCase.Failure)
                throw new InvalidOperationException("Cannot get Failure when case is Success");

            bool success = false;
            _payload!.DangerousAddRef(ref success);
            try
            {
                return ExtractPayloadValue<TFailure>();
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Extracts a typed value from the Result payload.
    /// Follows the same ownership pattern as SwiftOptional.Some:
    /// - Classes: dereference the class pointer from payload bytes, Arc.Retain for +1 ownership.
    /// - ISwiftObject (enums, non-frozen structs): heap-allocate a copy, NewFromPayload takes ownership.
    /// - Primitives/value types: heap-allocate a copy, free after marshal.
    /// Uses Swift metadata Kind to distinguish true classes from complex enums.
    /// </summary>
    private unsafe T ExtractPayloadValue<T>()
    {
        byte* sourcePayload = (byte*)_payload!.DangerousGetHandle();

        // True Swift classes: payload bytes contain the class pointer at offset 0.
        // Dereference and Arc.Retain for +1 ownership (SwiftClassHandle expects this).
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T)) &&
            !typeof(ISwiftStruct).IsAssignableFrom(typeof(T)))
        {
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
            if (metadata.Kind == TypeMetadataKind.Class)
            {
                IntPtr classPtr = *(IntPtr*)sourcePayload;
                Arc.Retain(classPtr);
                return SwiftMarshal.MarshalFromSwift<T>(classPtr);
            }
        }

        // Non-class ISwiftObject types (complex enums, non-frozen structs) and
        // ISwiftStruct types: NewFromPayload takes ownership of the buffer pointer.
        // Must heap-allocate — stackalloc would be freed on return.
        byte* heapCopy = (byte*)NativeMemory.Alloc(PayloadSize);
        new Span<byte>(sourcePayload, (int)PayloadSize).CopyTo(
            new Span<byte>(heapCopy, (int)PayloadSize));
        try
        {
            return SwiftMarshal.MarshalFromSwift<T>(new IntPtr(heapCopy));
        }
        finally
        {
            // ISwiftObject.NewFromPayload takes ownership of the buffer
            // (stores it in SwiftSafeHandle which frees on dispose).
            // Only free for non-ISwiftObject types (primitives, tuples, etc.)
            if (!typeof(ISwiftObject).IsAssignableFrom(typeof(T)))
            {
                NativeMemory.Free(heapCopy);
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

    private void ThrowIfCSharpOnly()
    {
        if (_payload is null)
            throw new InvalidOperationException(
                "This SwiftResult was created from C# (via FromSuccess/FromFailure) and has no native Swift payload. " +
                "It cannot be used for Swift interop operations.");
    }

    /// <summary>
    /// Internal class representing a success result that doesn't require Swift interop.
    /// Used when creating results from C# code (e.g., for throwing closure callbacks).
    /// </summary>
    private sealed class SuccessResult : SwiftResult<TSuccess, TFailure>
    {
        private readonly TSuccess _value;

        public SuccessResult(TSuccess value) : base(csharpOnly: true)
        {
            _value = value;
        }

        public override SwiftResultCase Case => SwiftResultCase.Success;
        public override bool IsSuccess => true;
        public override bool IsFailure => false;
        public override TSuccess Success => _value;
        public override TFailure Failure => throw new InvalidOperationException("Cannot get Failure when case is Success");
    }

    /// <summary>
    /// Internal class representing a failure result that doesn't require Swift interop.
    /// Used when creating results from C# code (e.g., for throwing closure callbacks).
    /// </summary>
    private sealed class FailureResult : SwiftResult<TSuccess, TFailure>
    {
        private readonly TFailure _error;

        public FailureResult(TFailure error) : base(csharpOnly: true)
        {
            _error = error;
        }

        public override SwiftResultCase Case => SwiftResultCase.Failure;
        public override bool IsSuccess => false;
        public override bool IsFailure => true;
        public override TSuccess Success => throw new InvalidOperationException("Cannot get Success when case is Failure");
        public override TFailure Failure => _error;
    }
}

internal static class PInvokesForSwiftResult
{
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss6ResultOMa")]
    public static extern TypeMetadata _MetadataAccessor(TypeMetadataRequest request, TypeMetadata successMetadata, TypeMetadata failureMetadata);
}
