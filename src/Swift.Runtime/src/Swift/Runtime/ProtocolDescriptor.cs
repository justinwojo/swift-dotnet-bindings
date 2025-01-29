// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Represents a Swift protocol descriptor.
/// </summary>
public readonly struct ProtocolDescriptor : IEquatable<ProtocolDescriptor>
{
    private readonly IntPtr _handle;

    private ProtocolDescriptor(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// An empty / invalid protocol descriptor.
    /// </summary>
    public readonly static ProtocolDescriptor Zero = default;

    /// <summary>
    /// Returns true if and only if the protocol descriptor is valid.
    /// </summary>
    public bool IsValid => _handle != IntPtr.Zero;

    /// <inheritdoc/>
    public bool Equals(ProtocolDescriptor other)
    {
        return _handle == other._handle;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is ProtocolDescriptor other && Equals(other);
    }

    /// <summary>
    /// Determines whether two <see cref="ProtocolDescriptor"/> instances are equal.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>
    /// <c>true</c> if the two instances are equal; otherwise, <c>false</c>.
    /// </returns>
    public static bool operator ==(ProtocolDescriptor left, ProtocolDescriptor right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Determines whether two <see cref="ProtocolDescriptor"/> instances are not equal.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>
    /// <c>true</c> if the two instances are not equal; otherwise, <c>false</c>.
    /// </returns>
    public static bool operator !=(ProtocolDescriptor left, ProtocolDescriptor right)
    {
        return !(left == right);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return _handle.GetHashCode();
    }

    /// <summary>
    /// Attempts to obtain a <see cref="ProtocolDescriptor"/> for the specified type and protocol.
    /// </summary>
    /// <typeparam name="TProtocol">The interface type representing the protocol.</typeparam>
    /// <param name="result">
    /// When this method returns, contains the <see cref="ProtocolDescriptor"/> if successful;
    /// otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the <see cref="ProtocolDescriptor"/> was found; otherwise, <c>false</c>.
    /// </returns>
    public static bool TryGet<TProtocol>([NotNullWhen(true)] out ProtocolDescriptor? result)
        where TProtocol : ISwiftProtocol
    {
        var type = typeof(TProtocol);

        if (typeof(ISwiftProtocol).IsAssignableFrom(type))
        {
            var helperType = typeof(ProtocolDescriptorHelper<>).MakeGenericType(typeof(TProtocol));
            var candidate = (ProtocolDescriptor)helperType.GetMethod("GetProtocolDescriptor")!.Invoke(null, null)!;

            // GetProtocolDescriptor can return an IntPtr.Zero
            if (candidate.IsValid)
            {
                result = candidate;
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Loads a <see cref="ProtocolDescriptor"/> from a symbol in the specified library.
    /// </summary>
    /// <param name="libraryName">The name of the library to load.</param>
    /// <param name="symbolName">The name of the symbol to retrieve.</param>
    /// <returns>
    /// A <see cref="ProtocolDescriptor"/> representing the loaded symbol.
    /// </returns>
    /// <exception cref="SwiftRuntimeException">
    /// Thrown when the specified library or symbol cannot be loaded.
    /// </exception>
    public static ProtocolDescriptor LoadFromSymbol(string libraryName, string symbolName)
    {
        IntPtr libraryHandle = IntPtr.Zero;

        try
        {
            if (!NativeLibrary.TryLoad(libraryName, typeof(ProtocolDescriptor).Assembly, null, out libraryHandle))
            {

                throw new SwiftRuntimeException($"Unable to load library: {libraryName}");
            }

            if (NativeLibrary.TryGetExport(libraryHandle, symbolName, out var handle))
            {
                return new ProtocolDescriptor(handle);
            }

            throw new SwiftRuntimeException($"Unable to find symbol: {symbolName} in library: {libraryName}");
        }
        finally
        {
            NativeLibrary.Free(libraryHandle);
        }
    }
}
