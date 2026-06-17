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
            // Determine inline vs out-of-line storage with the SAME criterion the write side uses
            // (ExistentialContainerFactory.MarshalPayload): a value is stored inline only when it fits
            // the 3-word buffer AND its value-witness IsNonInline flag is clear. A size-only check would
            // mis-route a small-but-non-inline type (odd alignment / not bitwise-takable) to the inline
            // branch and read boxed memory as inline bytes.
            var vwt = expected.ValueWitnessTable;
            var size = (int)vwt->Size;
            var isNonInline = (vwt->Flags & ValueWitnessFlags.IsNonInline) != 0;
            if (size <= ExistentialContainerFactory.MaxInlinePayloadSize && !isNonInline)
            {
                // Inline storage: value data is in payload0/payload1/payload2.
                // Pin a local copy and pass a pointer to the start (payload0 offset = 0).
                var containerCopy = _container;
                return (T)SwiftObjectHelper<T>.NewFromPayload((IntPtr)Unsafe.AsPointer(ref containerCopy));
            }
            else
            {
                // Out-of-line storage: payload0 is a swift_allocBox heap object (refcount header +
                // value), the exact inverse of MarshalPayload's write path, which stores boxPair.HeapObject
                // — NOT boxPair.Buffer — in payload0. Project past the box header to the value buffer before
                // reading it; passing the raw heap-object pointer to NewFromPayload's value-witness
                // InitializeWithCopy reads the box's metadata/refcount words as value bytes and faults.
                var valuePtr = swift_projectBox(_container.Payload0);
                return (T)SwiftObjectHelper<T>.NewFromPayload(valuePtr);
            }
        }
    }

    /// <summary>
    /// Projects a Swift box (the heap object stored in an out-of-line existential payload by
    /// <c>swift_allocBox</c>) to the address of the value it holds — the inverse of
    /// <see cref="ExistentialContainerFactory"/>'s <c>swift_allocBox</c> write path. Read-only:
    /// does not retain or release the box.
    /// </summary>
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_projectBox")]
    private static extern IntPtr swift_projectBox(IntPtr heapObject);

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
