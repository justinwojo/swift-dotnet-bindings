// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// The identity of one Swift type whose value is a single object reference: an Objective-C class,
/// or a string-backed Objective-C key type (an <c>NS_TYPED_ENUM</c> struct such as
/// <c>NSAttributedString.Key</c>, which stores its <c>NSString</c>). A binding declares one per such
/// type that it uses as a collection's key, set element, array element or dictionary value, and
/// registers it through <see cref="SwiftObjectKeys.Register{TIdentity}"/> (hashed positions) or
/// <see cref="SwiftObjectKeys.RegisterValue{TIdentity}"/> (every other position).
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
/// A value of the Swift type <typeparamref name="TIdentity"/> names, carried as its object pointer
/// inside a Swift collection: a dictionary key or set element, an array element or a dictionary
/// value. A Swift collection over the bare pointer would be one over <c>Swift.Int</c>: holding its
/// elements unretained and, as a dictionary or set, hashed by address, while Swift reads the same
/// storage as the real element type, releases what it replaces or destroys, bridges it to
/// Objective-C as that type and hashes by value. Carried as this type, the collection gets the
/// element type's own metadata (and, in a hashed position, its <c>Hashable</c> witness table), so
/// both sides retain, release, bridge, hash and compare alike.
/// </summary>
/// <remarks>
/// Ownership follows a Swift value of the key type. Built from a C# object, the carrier borrows the
/// object's reference: marshalling it into Swift memory copies it (+1) through the key type's value
/// witness, as any Swift value is copied. Read out of a Swift collection, the carrier borrows too: the
/// runtime releases the copy the collection handed over as it reads it, so a reader that keeps the
/// object takes its own reference (a retaining <c>GetNSObject</c>), and a carrier nobody keeps needs no
/// cleanup. A carrier read out of a collection is therefore valid only while that collection holds the
/// key, as is any object pointer read out of a raw Swift collection: convert it before removing the key
/// or releasing the collection.
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
    /// Registers the type <typeparamref name="TIdentity"/> names for positions that do not hash it
    /// (an array element, a dictionary value), from the type's own metadata, which the binding's
    /// wrapper returns. A type that is also a key or set element is registered through
    /// <see cref="Register{TIdentity}"/> instead, which registers the same metadata.
    /// </summary>
    /// <param name="metadata">The metadata of the element type.</param>
    /// <exception cref="SwiftRuntimeException">The pointer is not the metadata of a one-word type that
    /// holds a reference.</exception>
    public static unsafe void RegisterValue<TIdentity>(IntPtr metadata)
        where TIdentity : ISwiftObjectKeyIdentity
    {
        if (metadata == IntPtr.Zero)
            throw new SwiftRuntimeException($"No metadata for the element type of {typeof(TIdentity).FullName}.");
        var elementMetadata = TypeMetadata.FromHandle(metadata);
        if (!elementMetadata.IsValid || elementMetadata.Size != (nuint)IntPtr.Size || !elementMetadata.ValueWitnessTable->IsNonPOD)
            throw new SwiftRuntimeException($"The metadata for {typeof(TIdentity).FullName} is not a one-word type that holds a reference.");
        TypeMetadata.RegisterMetadata(typeof(SwiftObjectKey<TIdentity>), elementMetadata);
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
