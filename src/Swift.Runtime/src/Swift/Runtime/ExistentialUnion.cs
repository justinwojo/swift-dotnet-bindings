// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Wrapper for protocol existential containers when the protocol has associated types
/// or Self requirements (PAT protocols) but all concrete conformers are known at
/// binding generation time. Provides type-safe try-cast accessors to each conformer,
/// using Swift type metadata comparison to identify the concrete type inside the container.
///
/// Unlike proxy classes (which implement the protocol interface), ExistentialUnion is used
/// when the protocol cannot be represented as a C# interface due to PAT constraints.
/// The generator emits concrete try-cast calls like <c>union.As&lt;ChairType&gt;()</c>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ExistentialUnion : ISwiftExistentialConvertible<ExistentialContainer1>
{
    private readonly ExistentialContainer1 _container;

    /// <summary>
    /// Creates an ExistentialUnion wrapping a single-protocol existential container.
    /// </summary>
    /// <param name="container">The existential container from the Swift ABI.</param>
    public ExistentialUnion(ExistentialContainer1 container)
    {
        _container = container;
    }

    /// <summary>
    /// Gets the underlying existential container for parameter marshalling.
    /// </summary>
    public ExistentialContainer1 GetExistentialContainer() => _container;

    /// <summary>
    /// Gets the Swift type metadata for the value inside the container.
    /// This identifies which concrete conformer is stored.
    /// </summary>
    public TypeMetadata ObjectMetadata => _container.ObjectMetadata;

    /// <summary>
    /// Attempts to cast the existential value to a specific concrete conformer type.
    /// Compares the container's type metadata against the expected conformer's metadata.
    /// If they match, creates a new instance of <typeparamref name="T"/> from the container payload.
    /// </summary>
    /// <typeparam name="T">The concrete conformer type (must implement ISwiftObject).</typeparam>
    /// <returns>An instance of <typeparamref name="T"/> if the container holds that type; null otherwise.</returns>
    public T? As<T>() where T : class, ISwiftObject
    {
        var expected = SwiftObjectHelper<T>.GetTypeMetadata();
        if (!_container.ObjectMetadata.Equals(expected))
            return null;

        unsafe
        {
            // Determine if the value is stored inline in the payload buffer
            // or heap-allocated. Inline threshold: 3 machine words (24 bytes on 64-bit).
            var vwt = expected.ValueWitnessTable;
            if (vwt->Size <= (nuint)(3 * IntPtr.Size))
            {
                // Inline storage: value data is in payload0/payload1/payload2.
                // Pin a local copy and pass a pointer to the start (payload0 offset = 0).
                var containerCopy = _container;
                return (T)SwiftObjectHelper<T>.NewFromPayload((IntPtr)Unsafe.AsPointer(ref containerCopy));
            }
            else
            {
                // Heap storage: payload0 is a pointer to the heap-allocated value.
                return (T)SwiftObjectHelper<T>.NewFromPayload(_container.Payload0);
            }
        }
    }

    /// <summary>
    /// Attempts to cast the existential value to a specific concrete conformer type.
    /// </summary>
    /// <typeparam name="T">The concrete conformer type (must implement ISwiftObject).</typeparam>
    /// <param name="result">The cast result if successful.</param>
    /// <returns>True if the cast succeeded; false otherwise.</returns>
    public bool TryCast<T>(out T? result) where T : class, ISwiftObject
    {
        result = As<T>();
        return result != null;
    }
}
