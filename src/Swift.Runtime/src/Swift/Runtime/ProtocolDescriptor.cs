// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Represents a Swift protocol descriptor (<c>$s...Mp</c> symbols).
/// This describes the protocol itself, as opposed to <see cref="ProtocolConformanceDescriptor"/>
/// which describes a specific type's conformance to a protocol.
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
                // Fallback: try @rpath framework path. On iOS device, the DllImport resolver
                // that maps library names to framework paths is registered on the binding
                // assembly, not Swift.Runtime. NativeLibrary.TryLoad with the bare name
                // won't find it, but the @rpath framework path will. Mirrors the sibling
                // fallback in ProtocolConformanceDescriptor.LoadFromSymbol.
                var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
                if (!NativeLibrary.TryLoad(frameworkPath, out libraryHandle))
                {
                    throw new SwiftRuntimeException($"Unable to load library: {libraryName}");
                }
            }

            if (NativeLibrary.TryGetExport(libraryHandle, symbolName, out var handle))
            {
                return new ProtocolDescriptor(handle);
            }

            throw new SwiftRuntimeException($"Unable to find symbol: {symbolName} in library: {libraryName}");
        }
        finally
        {
            // The library handle is only needed to resolve the symbol export above; once
            // TryGetExport returns the symbol address, the image stays loaded via dyld's
            // reference count, so freeing this transient handle is safe. If this method
            // is ever refactored to retain the library handle alongside the symbol, this
            // unconditional Free becomes a use-after-free trap — return the handle in
            // the success path before relaxing it.
            NativeLibrary.Free(libraryHandle);
        }
    }
}
