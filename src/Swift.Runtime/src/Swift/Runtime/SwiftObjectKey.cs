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

    /// <summary>
    /// A carrier of the same type over <paramref name="handle"/>, kept valid by <paramref name="owner"/>
    /// (boxed: called on a default instance by code that knows the carrier only as a type argument).
    /// </summary>
    object WithOwner(IntPtr handle, SwiftObjectReference? owner);

    /// <summary>The reference that keeps the carrier's object alive, if the carrier holds one.</summary>
    SwiftObjectReference? Owner { get; }
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
/// witness, as any Swift value is copied. Read out of a Swift collection, the carrier holds the
/// reference the collection handed over, in an owner it carries along: whoever holds the carrier
/// (or a copy of it) holds the object, however long the collection lives or whatever happens to it
/// after the read, and the reference is released when the last copy of the carrier is collected. A
/// reader that wraps the object takes a reference of its own through
/// <see cref="SwiftObjectKeys.TakeReference{TIdentity}"/>. The carrier of an optional type
/// (<c>Optional&lt;NSUUID&gt;</c>) holds a null pointer for <c>nil</c>.
/// </remarks>
/// <typeparam name="TIdentity">The binding's identity for the Swift key type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public readonly struct SwiftObjectKey<TIdentity> : ISwiftObjectKeyCarrier, IEquatable<SwiftObjectKey<TIdentity>>
    where TIdentity : ISwiftObjectKeyIdentity
{
    private readonly IntPtr _handle;
    private readonly SwiftObjectReference? _owner;

    /// <summary>Carries the object pointer <paramref name="handle"/>, borrowing the caller's reference.</summary>
    public SwiftObjectKey(IntPtr handle)
    {
        _handle = handle;
        _owner = null;
    }

    private SwiftObjectKey(IntPtr handle, SwiftObjectReference? owner)
    {
        _handle = handle;
        _owner = owner;
    }

    /// <summary>
    /// The object pointer. A carrier read out of a Swift collection keeps the object alive only while
    /// the carrier itself is reachable, so keep it alive (<see cref="GC.KeepAlive"/>) for as
    /// long as the pointer is used.
    /// </summary>
    public IntPtr Handle => _handle;

    SwiftObjectReference? ISwiftObjectKeyCarrier.Owner => _owner;

    object ISwiftObjectKeyCarrier.WithOwner(IntPtr handle, SwiftObjectReference? owner) => new SwiftObjectKey<TIdentity>(handle, owner);

    /// <summary>Whether both carry the same object pointer (the owner is not part of the value).</summary>
    public bool Equals(SwiftObjectKey<TIdentity> other) => _handle == other._handle;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SwiftObjectKey<TIdentity> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _handle.GetHashCode();
}

/// <summary>
/// One Swift reference (+1) on the object a <see cref="SwiftObjectKey{TIdentity}"/> read out of a
/// Swift collection carries, shared by every copy of that carrier and released once the last of them
/// is collected. It is never handed over: copies of a carrier are untracked struct copies, so any of
/// them may still be held, and each must keep naming a live object.
/// </summary>
internal sealed class SwiftObjectReference
{
    private readonly IntPtr _handle;

    internal SwiftObjectReference(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Releases the reference from the finalizer thread: every carried value is one Swift or
    /// Objective-C object pointer, released through the finalizer-safe trampoline, and left alone
    /// once the process is exiting and the Swift runtime may be torn down.
    /// </summary>
    ~SwiftObjectReference()
    {
        if (!SwiftExitGuard.IsProcessExiting)
            Arc.UnknownObjectReleaseFinalizerSafe(_handle);
    }
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
    /// Registers the <c>Hashable</c> conformance a Swift class's wrapper <typeparamref name="T"/> is
    /// hashed by as a dictionary key or a set element, from the metadata of <c>Set&lt;Class&gt;</c> the
    /// binding's wrapper returns. The conformance may be the class's own, inherited from a superclass
    /// or <c>NSObject</c>'s, so the binding cannot name its descriptor; <c>Set&lt;Class&gt;</c>'s metadata
    /// holds the one the compiler resolved, as its generic argument's witness table.
    /// </summary>
    /// <param name="hashableSetMetadata">The metadata of <c>Swift.Set&lt;Class&gt;</c>.</param>
    /// <exception cref="SwiftRuntimeException">The pointer is not <c>Set</c> metadata over
    /// <typeparamref name="T"/>'s class and a <c>Hashable</c> witness table.</exception>
    public static unsafe void RegisterHashableClass<T>(IntPtr hashableSetMetadata)
        where T : class, ISwiftObject
    {
        if (hashableSetMetadata == IntPtr.Zero)
            throw new SwiftRuntimeException($"No Set metadata for {typeof(T).FullName}.");

        // Struct metadata: [kind][nominal type descriptor][Element][Element: Hashable].
        var words = (IntPtr*)hashableSetMetadata;
        var elementMetadata = TypeMetadata.FromHandle(words[2]);
        var hashable = ProtocolWitnessTable.FromHandle(words[3]);
        if (!elementMetadata.IsValid || !hashable.IsValid)
            throw new SwiftRuntimeException($"The Set metadata for {typeof(T).FullName} does not hold an element type and its Hashable conformance.");
        if (elementMetadata.Handle != T.GetTypeMetadata().Handle)
            throw new SwiftRuntimeException($"The Set metadata for {typeof(T).FullName} is not over its class.");

        var rebuilt = SwiftSetPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, elementMetadata, hashable);
        if (rebuilt.Handle != hashableSetMetadata)
            throw new SwiftRuntimeException($"The Set metadata for {typeof(T).FullName} did not round-trip through its class and Hashable conformance.");

        WitnessTableDispatcher.Register(typeof(T), typeof(ISwiftHashable), hashable);
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
        // The copy is taken while the carrier's owner, if any, still holds the object.
        GC.KeepAlive(carrier.Owner);
    }

    /// <summary>
    /// A carrier of <typeparamref name="T"/> that takes over the +1 reference <paramref name="handle"/>
    /// holds (a value moved out of a Swift slot), released when the carrier is collected.
    /// </summary>
    internal static T AdoptCarrier<T>(IntPtr handle)
    {
        var owner = handle == IntPtr.Zero ? null : new SwiftObjectReference(handle);
        return (T)((ISwiftObjectKeyCarrier)default(T)!).WithOwner(handle, owner);
    }

    /// <summary>
    /// A carrier of <typeparamref name="T"/> over the value at <paramref name="source"/>, which the
    /// caller keeps: the carrier takes its own reference (+1), released when it is collected.
    /// </summary>
    internal static unsafe T CopyCarrier<T>(IntPtr source, TypeMetadata metadata)
    {
        IntPtr copy;
        metadata.ValueWitnessTable->InitializeWithCopy(&copy, (void*)source, metadata);
        return AdoptCarrier<T>(copy);
    }

    /// <summary>
    /// A new +1 reference on the object <paramref name="key"/> carries, for a wrapper that adopts it
    /// (<c>GetINativeObject(handle, owns: true)</c>), or zero for a <c>nil</c> optional. The value is
    /// copied through its type while the carrier's own reference (if it holds one) keeps the object
    /// alive; that reference stays with the carrier, whose other copies may still be held.
    /// </summary>
    /// <param name="key">The carrier.</param>
    /// <returns>An owned reference the caller must adopt, or <see cref="IntPtr.Zero"/>.</returns>
    public static unsafe IntPtr TakeReference<TIdentity>(SwiftObjectKey<TIdentity> key)
        where TIdentity : ISwiftObjectKeyIdentity
    {
        IntPtr handle = key.Handle;
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftObjectKey<TIdentity>>();
        IntPtr copy;
        metadata.ValueWitnessTable->InitializeWithCopy(&copy, &handle, metadata);
        GC.KeepAlive(((ISwiftObjectKeyCarrier)key).Owner);
        return copy;
    }

    /// <summary>
    /// A wrapper of the Swift class the carrier of its optional names, holding its own reference (see
    /// <see cref="TakeReference{TIdentity}"/>), or <c>null</c> for a <c>nil</c> carrier.
    /// </summary>
    /// <typeparam name="TIdentity">The binding's identity for the optional class type.</typeparam>
    /// <typeparam name="T">The class's wrapper.</typeparam>
    public static unsafe T? TakeSwiftObject<TIdentity, T>(SwiftObjectKey<TIdentity> key)
        where TIdentity : ISwiftObjectKeyIdentity
        where T : class, ISwiftObject
    {
        IntPtr handle = TakeReference(key);
        if (handle == IntPtr.Zero)
            return null;
        // A Swift class's wrapper is built from the object pointer itself and adopts its +1
        // (PayloadConstructionSemantics.Adopt), so the owned reference is handed over as is.
        return SwiftMarshal.MarshalFromSwiftObject<T>(handle);
    }
}
