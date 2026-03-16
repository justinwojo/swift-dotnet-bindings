// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Interface for concrete Swift types that can be boxed into existential containers
/// for protocol parameter passing. Unlike <see cref="ISwiftExistentialConvertible{TContainer}"/>
/// (which returns a pre-existing container from proxy types), this interface constructs
/// a new existential container at runtime using the type's metadata and protocol witness table.
/// </summary>
public interface IExistentialBoxable
{
    /// <summary>
    /// Creates an <see cref="ExistentialContainer1"/> for this value conforming to the specified protocol.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., IBlockMode).</typeparam>
    /// <returns>An ExistentialContainer1 with payload, type metadata, and protocol witness table.</returns>
    ExistentialContainer1 BoxAsExistential1<TProtocol>() where TProtocol : class;
}
