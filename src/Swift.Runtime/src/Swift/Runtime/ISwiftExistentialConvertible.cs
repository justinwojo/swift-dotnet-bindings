// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Interface for types that can provide their underlying Swift existential container.
/// Protocol proxy classes implement this interface to enable extraction of the container
/// when passing protocol-typed parameters to P/Invoke.
/// </summary>
/// <typeparam name="TContainer">The existential container struct type (e.g., ExistentialContainer1).</typeparam>
public interface ISwiftExistentialConvertible<TContainer> where TContainer : struct
{
    /// <summary>
    /// Gets the underlying Swift existential container.
    /// </summary>
    /// <returns>The existential container holding the Swift value, metadata, and witness tables.</returns>
    TContainer GetExistentialContainer();
}
