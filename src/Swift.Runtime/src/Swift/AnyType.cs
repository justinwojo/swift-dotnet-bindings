// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Swift.Runtime;

#nullable enable

namespace Swift;

/// <summary>
/// Represents placeholder for Swift type
/// </summary>
public struct AnyType : ISwiftObject
{
    private SwiftSafeHandle<AnyType> _payload = SwiftSafeHandle<AnyType>.Zero;
    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        throw new InvalidOperationException("Cannot get type metadata for AnyType");
    }
    public AnyType(SwiftHandle payload)
    {
        _payload = new SwiftSafeHandle<AnyType>(payload);
    }
    public SwiftSafeHandle<AnyType> Payload => _payload;

    /// <summary>
    /// Creates a new SwiftOptional from a Swift payload
    /// </summary>
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
    {
        return new AnyType(payload);
    }

    /// <summary>
    /// Marshals this object to a Swift destination
    /// </summary>
    /// <param name="swiftDestSpan"></param>
    /// <returns></returns>
    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        throw new InvalidOperationException("Cannot marshal AnyType to Swift");
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        return ProtocolConformanceDescriptor.Zero;
    }

    /// <summary>
    /// Returns a nullable version of this AnyType.
    /// This is used for Optional-wrapped existential types where the inner type cannot be determined.
    /// </summary>
    /// <returns>A nullable AnyType (always returns null since AnyType represents an unsupported type).</returns>
    public AnyType? ToNullable()
    {
        // AnyType represents an unsupported type placeholder.
        // When used in Optional context, we return null since we can't properly represent the value.
        return null;
    }

    /// <inheritdoc/>
    public void Dispose() => _payload?.Dispose();
}
