// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Swift.Runtime;

/// <summary>
/// Interface for types that can provide their underlying Swift existential container.
/// This type is an implementation detail of the Swift/.NET interop layer and should not be used directly
/// by consumers. Protocol proxy classes implement this interface internally.
/// </summary>
/// <typeparam name="TContainer">The existential container struct type (e.g., ExistentialContainer1).</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISwiftExistentialConvertible<TContainer> where TContainer : struct
{
    /// <summary>
    /// Gets the underlying Swift existential container.
    /// </summary>
    /// <returns>The existential container holding the Swift value, metadata, and witness tables.</returns>
    TContainer GetExistentialContainer();
}
