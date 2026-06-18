// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;

namespace Swift.ManagedSettings;

/// <summary>
/// C# projection of ManagedSettings.Token&lt;Kind&gt;, an opaque non-frozen generic struct
/// used as a typed identifier in the Family Controls / Managed Settings frameworks.
/// Tokens are opaque — you receive them from system APIs and pass them to other APIs,
/// but cannot construct or inspect their contents from C#.
/// </summary>
/// <typeparam name="T">The marker type distinguishing token kinds
/// (Application, ActivityCategory, or WebDomain).</typeparam>
public sealed class Token<T> : ISwiftObject, ISwiftStruct, IDisposable where T : class
{
    private SwiftSafeHandle<Token<T>> _payload = SwiftSafeHandle<Token<T>>.Zero;
    private bool _disposed;
    private static TypeMetadata? _cachedMetadata;

    // Routes through SwiftObjectHelper so the NewFromPayload factory is registered with
    // NewFromPayloadDispatcher on first use of each closed generic instantiation. Without
    // this, MarshalFromSwift&lt;Token&lt;SomeMarker&gt;&gt; falls back to reflection on NativeAOT
    // which can miss explicit interface implementations after trimming. Mirrors Measurement&lt;T&gt;.
    private static readonly nuint _payloadSize = SwiftObjectHelper<Token<T>>.GetTypeMetadata().Size;

    /// <summary>The safe handle wrapping the native Swift storage for this token.</summary>
    public SwiftSafeHandle<Token<T>> Payload => _payload;

    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
        => _cachedMetadata ??= InitializeMetadata();

    private static TypeMetadata InitializeMetadata()
    {
        // Token<T>'s generic arg is a ManagedSettings marker type.
        // Get the marker type's metadata via its mangled name accessor.
        var markerMetadata = TokenInterop.GetMarkerMetadata(typeof(T).Name);
        var metadata = TokenInterop.GetTokenMetadata(markerMetadata);
        _cachedMetadata = metadata;
        return metadata;
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        var metadata = _cachedMetadata ??= InitializeMetadata();
        unsafe
        {
            var size = (int)metadata.Size;
            var heapCopy = NativeMemory.Alloc((nuint)size);
            metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, (void*)handle, metadata);
            return new Token<T>((IntPtr)heapCopy);
        }
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Copy;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = _cachedMetadata ??= InitializeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
            throw new ArgumentException($"Span size mismatch: expected {(int)metadata.Size}, got {swiftDestSpan.Length}");
        unsafe
        {
            fixed (void* dest = swiftDestSpan)
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(dest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success) _payload.DangerousRelease();
                }
            }
        }
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        => throw new SwiftRuntimeException($"Protocol conformance not implemented for Token<{typeof(T).Name}> and {typeof(TProtocol).Name}");

    internal Token(IntPtr handle) => _payload = new SwiftSafeHandle<Token<T>>(handle);

    /// <summary>Releases the native Swift storage backing this token.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _payload.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Non-generic helper for Token metadata P/Invoke.
/// DllImport cannot be inside a generic type (CS7042).
/// </summary>
internal static class TokenInterop
{
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_Token_GetMetadata",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PInvoke_GetTokenMetadata(IntPtr markerMetadata);

    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_ManagedSettings_MarkerMetadata",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PInvoke_GetMarkerMetadata(int markerIndex);

    internal static TypeMetadata GetTokenMetadata(TypeMetadata markerMetadata)
        => TypeMetadata.FromHandle(PInvoke_GetTokenMetadata(markerMetadata.Handle));

    /// <summary>
    /// Resolves marker type metadata by name via the runtime's dlsym-based accessor.
    /// Returns TypeMetadata.Zero if ManagedSettings is not loaded (e.g. tvOS, Catalyst).
    /// </summary>
    internal static TypeMetadata GetMarkerMetadata(string markerTypeName)
    {
        int index = markerTypeName switch
        {
            "Application" => 0,
            "ActivityCategory" => 1,
            "WebDomain" => 2,
            _ => -1,
        };
        if (index < 0)
            return TypeMetadata.Zero;
        var handle = PInvoke_GetMarkerMetadata(index);
        return handle == IntPtr.Zero ? TypeMetadata.Zero : TypeMetadata.FromHandle(handle);
    }
}
