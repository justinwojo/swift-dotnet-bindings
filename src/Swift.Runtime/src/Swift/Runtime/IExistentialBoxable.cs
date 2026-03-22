// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Swift.Runtime;

/// <summary>
/// Interface for concrete Swift types that can be boxed into existential containers.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. The generated binding code handles existential boxing automatically.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IExistentialBoxable
{
    /// <summary>
    /// Creates an <see cref="ExistentialContainer1"/> for this value conforming to the specified protocol.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., IBlockMode).</typeparam>
    /// <returns>An ExistentialContainer1 with payload, type metadata, and protocol witness table.</returns>
    ExistentialContainer1 BoxAsExistential1<TProtocol>() where TProtocol : class;
}
