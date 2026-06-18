// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

#nullable enable

namespace Swift;

/// <summary>
/// Opaque wrapper for Swift's AnyHashable type-erased container.
/// AnyHashable wraps any Hashable value, preserving its identity for hashing/equality.
/// </summary>
public struct AnyHashable : ISwiftObject
{
    private SwiftSafeHandle<AnyHashable> _payload = SwiftSafeHandle<AnyHashable>.Zero;

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        throw new InvalidOperationException("Cannot get type metadata for AnyHashable");
    }

    public AnyHashable(SwiftHandle payload)
    {
        _payload = new SwiftSafeHandle<AnyHashable>(payload);
    }

    public SwiftSafeHandle<AnyHashable> Payload => _payload;

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        return new AnyHashable(payload);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Inline;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        throw new InvalidOperationException("Cannot marshal AnyHashable to Swift");
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        return ProtocolConformanceDescriptor.Zero;
    }

    public void Dispose() => _payload?.Dispose();
}
