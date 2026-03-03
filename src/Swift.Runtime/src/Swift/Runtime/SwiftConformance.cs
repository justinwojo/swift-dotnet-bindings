// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides runtime protocol conformance checking via the Swift runtime function
/// <c>swift_conformsToProtocol</c>. This enables dynamic conformance queries:
/// "does type X conform to protocol Y?" using only the type's metadata and the
/// protocol's descriptor, without requiring compile-time knowledge of every
/// (type, protocol) pair.
/// </summary>
public static class SwiftConformance
{
    /// <summary>
    /// Checks whether a Swift type conforms to a specified protocol at runtime.
    /// </summary>
    /// <param name="typeMetadata">The type metadata for the Swift type to check.</param>
    /// <param name="protocolDescriptor">The protocol descriptor for the protocol to check against.</param>
    /// <returns>
    /// <c>true</c> if the type conforms to the protocol; otherwise, <c>false</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="typeMetadata"/> or <paramref name="protocolDescriptor"/> is invalid (zero).
    /// </exception>
    public static bool ConformsToProtocol(TypeMetadata typeMetadata, ProtocolDescriptor protocolDescriptor)
    {
        if (!typeMetadata.IsValid)
            throw new ArgumentException("TypeMetadata is not valid.", nameof(typeMetadata));
        if (!protocolDescriptor.IsValid)
            throw new ArgumentException("ProtocolDescriptor is not valid.", nameof(protocolDescriptor));

        var witnessTable = swift_conformsToProtocol(typeMetadata, protocolDescriptor);
        return witnessTable.IsValid;
    }

    /// <summary>
    /// Attempts to get the witness table for a type's conformance to a protocol at runtime.
    /// </summary>
    /// <param name="typeMetadata">The type metadata for the Swift type to check.</param>
    /// <param name="protocolDescriptor">The protocol descriptor for the protocol to check against.</param>
    /// <param name="witnessTable">
    /// When this method returns, contains the <see cref="ProtocolWitnessTable"/> if the type conforms
    /// to the protocol; otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the type conforms to the protocol and a witness table was obtained;
    /// otherwise, <c>false</c>.
    /// </returns>
    public static bool TryGetWitnessTable(TypeMetadata typeMetadata, ProtocolDescriptor protocolDescriptor, out ProtocolWitnessTable? witnessTable)
    {
        if (!typeMetadata.IsValid || !protocolDescriptor.IsValid)
        {
            witnessTable = null;
            return false;
        }

        var result = swift_conformsToProtocol(typeMetadata, protocolDescriptor);
        if (result.IsValid)
        {
            witnessTable = result;
            return true;
        }

        witnessTable = null;
        return false;
    }

    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern ProtocolWitnessTable swift_conformsToProtocol(TypeMetadata typeMetadata, ProtocolDescriptor protocolDescriptor);
}
