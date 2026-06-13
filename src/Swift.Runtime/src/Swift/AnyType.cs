// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Swift.Runtime;

#nullable enable

namespace Swift;

/// <summary>
/// Represents a Swift type that could not be resolved to a concrete .NET projection.
/// </summary>
/// <remarks>
/// <para>
/// AnyType appears in generated bindings when the generator encounters a Swift type it cannot
/// map to a concrete C# type. Common causes include:
/// </para>
/// <list type="bullet">
/// <item><description>Generic type parameters that can't be resolved at binding time (e.g., <c>T</c>, <c>Element</c>)</description></item>
/// <item><description>Self-returning methods (Swift's <c>Self</c> type)</description></item>
/// <item><description>Types from unsupported Apple frameworks (SwiftUI, Combine)</description></item>
/// <item><description>Types from dependency frameworks that weren't provided during generation</description></item>
/// </list>
/// <para>
/// <b>What you can do:</b> If AnyType appears because a dependency framework is missing,
/// provide it via <c>&lt;SwiftFrameworkDependency&gt;</c> (MSBuild SDK) or
/// <c>--framework-dependency</c> (CLI) and regenerate. Check <c>binding-report.json</c>
/// for the specific skip reason and the original Swift type name.
/// </para>
/// <para>
/// Members whose signature contains AnyType are typically skipped by the generator.
/// If a member you need was skipped, check the binding report for alternatives.
/// </para>
/// </remarks>
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

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

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
