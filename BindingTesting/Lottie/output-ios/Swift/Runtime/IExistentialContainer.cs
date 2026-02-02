// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Interface for Swift existential containers.
/// An existential container is a value type that boxes any type conforming to a set of protocols.
///
/// Layout (standard existential container on 64-bit):
/// - Payload: 3 machine words (24 bytes) - inline storage or pointer to heap allocation
/// - TypeMetadata: 1 machine word (8 bytes) - pointer to type metadata
/// - WitnessTables: N machine words (8*N bytes) - pointers to protocol witness tables
///
/// If the contained value is 24 bytes or less (3 words), it's stored inline in the payload.
/// If larger, the payload contains a pointer to a heap-allocated copy.
/// </summary>
public interface IExistentialContainer
{
    /// <summary>
    /// Gets or sets the first word of the payload.
    /// </summary>
    IntPtr Payload0 { get; set; }

    /// <summary>
    /// Gets or sets the second word of the payload.
    /// </summary>
    IntPtr Payload1 { get; set; }

    /// <summary>
    /// Gets or sets the third word of the payload.
    /// </summary>
    IntPtr Payload2 { get; set; }

    /// <summary>
    /// Gets or sets the type metadata of the boxed value.
    /// </summary>
    TypeMetadata ObjectMetadata { get; set; }

    /// <summary>
    /// Gets or sets the protocol witness table at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the witness table.</param>
    /// <returns>A handle to the protocol witness table.</returns>
    IntPtr this[int index] { get; set; }

    /// <summary>
    /// Gets the number of protocol witness tables in this container.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the size of this existential container in bytes.
    /// </summary>
    int SizeOf { get; }

    /// <summary>
    /// Copies the existential container to the specified memory location.
    /// </summary>
    /// <param name="memory">The destination memory pointer.</param>
    /// <returns>The destination memory pointer.</returns>
    IntPtr CopyTo(IntPtr memory);

    /// <summary>
    /// Copies the contents of this container into another container.
    /// </summary>
    /// <typeparam name="T">The type of the destination container.</typeparam>
    /// <param name="container">The destination container.</param>
    /// <exception cref="ArgumentException">Thrown if the containers have different sizes.</exception>
    void CopyTo<T>(ref T container) where T : struct, IExistentialContainer;
}
