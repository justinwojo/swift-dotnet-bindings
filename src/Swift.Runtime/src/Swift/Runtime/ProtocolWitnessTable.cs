// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Represents a Swift protocol witness table.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct ProtocolWitnessTable : IEquatable<ProtocolWitnessTable>
{
    private readonly IntPtr _handle;

    private ProtocolWitnessTable(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Gets the underlying native handle of the protocol witness table.
    /// </summary>
    public IntPtr Handle => _handle;

    /// <summary>
    /// An empty / invalid protocol witness table.
    /// </summary>
    public readonly static ProtocolWitnessTable Zero = default;

    /// <summary>
    /// Returns true if and only if the protocol witness table is valid.
    /// </summary>
    public bool IsValid => _handle != IntPtr.Zero;

    /// <inheritdoc/>
    public bool Equals(ProtocolWitnessTable other)
    {
        return _handle == other._handle;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is ProtocolWitnessTable other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return _handle.GetHashCode();
    }

    /// <summary>
    /// Determines whether two <see cref="ProtocolWitnessTable"/> instances are equal.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>
    /// <c>true</c> if the two tables represent the same native handle; otherwise, <c>false</c>.
    /// </returns>
    public static bool operator ==(ProtocolWitnessTable left, ProtocolWitnessTable right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Determines whether two <see cref="ProtocolWitnessTable"/> instances are not equal.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>
    /// <c>true</c> if the two tables do not represent the same native handle; otherwise, <c>false</c>.
    /// </returns>
    public static bool operator !=(ProtocolWitnessTable left, ProtocolWitnessTable right)
    {
        return !(left == right);
    }

    /// <summary>
    /// Attempts to get a <see cref="ProtocolWitnessTable"/> for a specified type and protocol.
    /// </summary>
    /// <typeparam name="TType">The type for which to get the witness table.</typeparam>
    /// <typeparam name="TProtocol">The Swift protocol type.</typeparam>
    /// <param name="result">
    /// When this method returns, contains the <see cref="ProtocolWitnessTable"/> if found;
    /// otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the table was found; otherwise, <c>false</c>.
    /// </returns>

    public static bool TryGet<TType, TProtocol>([NotNullWhen(true)] out ProtocolWitnessTable? result)
        where TProtocol : class
    {
        if (!TypeMetadata.TryGetTypeMetadata<TType>(out var metadata))
        {
            result = null;
            return false;
        }

        if (!ProtocolConformanceDescriptor.TryGet<TType, TProtocol>(out var conformanceDescriptor))
        {
            result = null;
            return false;
        }

        result = GetProtocolWitnessTable(conformanceDescriptor.Value, metadata.Value);
        return true;
    }

    /// <summary>
    /// Gets the <see cref="ProtocolWitnessTable"/> for a specified type and protocol, or throws
    /// an exception if the table is not found.
    /// </summary>
    /// <typeparam name="TType">The type for which to get the witness table.</typeparam>
    /// <typeparam name="TProtocol">The Swift protocol type.</typeparam>
    /// <returns>
    /// The <see cref="ProtocolWitnessTable"/> if found.
    /// </returns>
    /// <exception cref="SwiftRuntimeException">
    /// Thrown if the protocol witness table cannot be obtained for the specified type and protocol.
    /// </exception>
    public static ProtocolWitnessTable GetOrThrow<TType, TProtocol>()
        where TProtocol : class
    {
        // Try pre-registered witness table first (populated by generated [ModuleInitializer] code).
        // This avoids reflection-based MakeGenericType on NativeAOT for types lacking ISwiftObject constraint
        // (e.g., TKey in SwiftDictionary, Element in SwiftSet).
        if (InteropServices.WitnessTableDispatcher.TryGet(typeof(TType), typeof(TProtocol), out var cached))
            return cached;

        if (!TryGet<TType, TProtocol>(out var result))
        {
            throw new SwiftRuntimeException($"Unable to get protocol witness table for {typeof(TType)} and {typeof(TProtocol)}");
        }

        return result.Value;
    }

    /// <summary>
    /// Runtime-safe dispatch: uses GetOrThrowDirect on NativeAOT (static virtual dispatch),
    /// GetOrThrow on Mono (reflection-safe). Call this when you have an ISwiftObject constraint
    /// and need to work on both runtimes.
    /// </summary>
    public static ProtocolWitnessTable GetOrThrowAuto<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        return SwiftRuntimeInfo.IsNativeAotRuntime
            ? GetOrThrowDirect<TType, TProtocol>()
            : GetOrThrow<TType, TProtocol>();
    }

    /// <summary>
    /// NativeAOT-safe overload that avoids MakeGenericType reflection.
    /// Uses <see cref="ProtocolConformanceDescriptor.TryGetDirect{TType, TProtocol}"/> which calls
    /// the static abstract method directly via the <see cref="ISwiftObject"/> constraint.
    /// </summary>
    public static ProtocolWitnessTable GetOrThrowDirect<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        // Check pre-registered witness table first (populated during module initialization).
        // This avoids LoadFromSymbol → swift_getWitnessTable at runtime, which can crash
        // on NativeAOT device due to library handle lifecycle issues.
        if (InteropServices.WitnessTableDispatcher.TryGet(typeof(TType), typeof(TProtocol), out var cached))
            return cached;

        if (!TypeMetadata.TryGetTypeMetadata<TType>(out var metadata))
        {
            throw new SwiftRuntimeException($"Unable to get type metadata for {typeof(TType)}");
        }

        if (!ProtocolConformanceDescriptor.TryGetDirect<TType, TProtocol>(out var conformanceDescriptor))
        {
            throw new SwiftRuntimeException($"Unable to get protocol conformance descriptor for {typeof(TType)} and {typeof(TProtocol)}");
        }

        return GetProtocolWitnessTable(conformanceDescriptor.Value, metadata.Value);
    }

    /// <summary>
    /// Gets the protocol witness table for a given type and protocol.
    /// </summary>
    /// <param name="conformanceDescriptor">The protocol conformance descriptor.</param>
    /// <param name="typeMetadata">The type metadata.</param>
    /// <returns>The protocol witness table.</returns>
    internal static ProtocolWitnessTable GetProtocolWitnessTable(ProtocolConformanceDescriptor conformanceDescriptor, TypeMetadata typeMetadata)
        => swift_getWitnessTable(conformanceDescriptor, typeMetadata, IntPtr.Zero);

    /// <summary>
    /// Gets the protocol witness table for a given type and protocol.
    /// </summary>
    /// <param name="conformanceDescriptor">The protocol conformance descriptor.</param>
    /// <param name="typeMetadata">The type metadata.</param>
    /// <param name="instantiationArgs">The instantiation arguments used for conditional conformance.</param>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern ProtocolWitnessTable swift_getWitnessTable(ProtocolConformanceDescriptor conformanceDescriptor, TypeMetadata typeMetadata, IntPtr instantiationArgs);
}
