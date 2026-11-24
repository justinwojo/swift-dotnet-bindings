// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// The identity of one Swift <c>Hashable</c> type whose value is a single object reference: an
/// Objective-C class, or a string-backed Objective-C key type (an <c>NS_TYPED_ENUM</c> struct such as
/// <c>NSAttributedString.Key</c>, which stores its <c>NSString</c>). A binding declares one per such
/// type that it uses as a dictionary key or set element, and registers it through
/// <see cref="SwiftObjectKeys.Register{TIdentity}"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISwiftObjectKeyIdentity { }

/// <summary>
/// The non-generic view of <see cref="SwiftObjectKey{TIdentity}"/>, for the runtime's generic
/// collection code, which knows an element only by its C# type.
/// </summary>
internal interface ISwiftObjectKeyCarrier
{
    IntPtr Handle { get; }
}

/// <summary>
/// A dictionary key or set element of the Swift type <typeparamref name="TIdentity"/> names, carried
/// as its object pointer. A Swift collection over the bare pointer would be one over <c>Swift.Int</c>:
/// hashed by address and holding its keys unretained, while Swift reads the same storage as the real
/// key type and hashes by value. Carried as this type, the collection gets the key type's own
/// metadata and <c>Hashable</c> witness table, so both sides hash, compare and retain alike.
/// </summary>
/// <remarks>
/// Ownership follows a Swift value of the key type. Built from a C# object, the carrier borrows the
/// object's reference: marshalling it into Swift memory copies it (+1) through the key type's value
/// witness, as any Swift value is copied. Read out of a Swift collection, the carrier borrows too: the
/// runtime releases the copy the collection handed over as it reads it, so a reader that keeps the
/// object takes its own reference (a retaining <c>GetNSObject</c>), and a carrier nobody keeps needs no
/// cleanup.
/// </remarks>
/// <typeparam name="TIdentity">The binding's identity for the Swift key type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public readonly struct SwiftObjectKey<TIdentity> : ISwiftObjectKeyCarrier
    where TIdentity : ISwiftObjectKeyIdentity
{
    private readonly IntPtr _handle;

    /// <summary>Carries the object pointer <paramref name="handle"/>.</summary>
    public SwiftObjectKey(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>The object pointer.</summary>
    public IntPtr Handle => _handle;
}

/// <summary>
/// Registers the Swift metadata and <c>Hashable</c> conformance of each
/// <see cref="SwiftObjectKey{TIdentity}"/> a binding uses.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwiftObjectKeys
{
    /// <summary>
    /// Registers the key type <typeparamref name="TIdentity"/> names, from the metadata of
    /// <c>Set&lt;Key&gt;</c> the binding's wrapper returns. A clang-imported key type's metadata and
    /// its synthesized <c>Hashable</c> conformance are local to each image that uses them, and the
    /// runtime cannot look either up by name; <c>Set&lt;Key&gt;</c>'s metadata holds both, as its generic
    /// argument and that argument's witness table.
    /// </summary>
    /// <param name="hashableSetMetadata">The metadata of <c>Swift.Set&lt;Key&gt;</c>.</param>
    /// <exception cref="SwiftRuntimeException">The pointer is not <c>Set</c> metadata over a key and
    /// its <c>Hashable</c> witness table.</exception>
    public static unsafe void Register<TIdentity>(IntPtr hashableSetMetadata)
        where TIdentity : ISwiftObjectKeyIdentity
    {
        if (hashableSetMetadata == IntPtr.Zero)
            throw new SwiftRuntimeException($"No Set metadata for the key type of {typeof(TIdentity).FullName}.");

        // Struct metadata: [kind][nominal type descriptor][generic arguments...]. Set<Element: Hashable>
        // has one type argument followed by its one key-argument witness table.
        var words = (IntPtr*)hashableSetMetadata;
        var keyMetadata = TypeMetadata.FromHandle(words[2]);
        var hashable = ProtocolWitnessTable.FromHandle(words[3]);
        if (!keyMetadata.IsValid || !hashable.IsValid || keyMetadata.Size != (nuint)IntPtr.Size)
            throw new SwiftRuntimeException($"The Set metadata for {typeof(TIdentity).FullName} does not hold a one-word key type.");

        // Metadata is uniqued: instantiating Set over the two words read back yields the same
        // pointer only if they are the key type and its Hashable conformance.
        var rebuilt = SwiftSetPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, keyMetadata, hashable);
        if (rebuilt.Handle != hashableSetMetadata)
            throw new SwiftRuntimeException($"The Set metadata for {typeof(TIdentity).FullName} did not round-trip through its key type and Hashable conformance.");

        var carrier = typeof(SwiftObjectKey<TIdentity>);
        TypeMetadata.RegisterMetadata(carrier, keyMetadata);
        WitnessTableDispatcher.Register(carrier, typeof(ISwiftHashable), hashable);
    }

    /// <summary>
    /// Writes an owned (+1) copy of the carrier's value into <paramref name="destination"/>, through
    /// the key type's value witness.
    /// </summary>
    internal static unsafe void InitializeOwnedCopy(ISwiftObjectKeyCarrier carrier, TypeMetadata metadata, void* destination)
    {
        IntPtr handle = carrier.Handle;
        metadata.ValueWitnessTable->InitializeWithCopy(destination, &handle, metadata);
    }
}
