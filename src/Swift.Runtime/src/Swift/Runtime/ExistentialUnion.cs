// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

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
public sealed class ExistentialUnion : ISwiftExistentialConvertible<ExistentialContainer1>, IDisposable
{
    private ExistentialContainer1 _container;
    private readonly bool _ownsContainer;
    private readonly bool _classBound;
    private readonly TypeMetadata _containerMetadata;
    private int _released;

    /// <summary>
    /// Creates an ExistentialUnion over a borrowed single-protocol existential container: the
    /// caller keeps ownership and must keep the value alive while the union is used.
    /// </summary>
    /// <param name="container">The existential container from the Swift ABI.</param>
    public ExistentialUnion(ExistentialContainer1 container)
    {
        _container = container;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates an ExistentialUnion that adopts a container Swift returned at <c>+1</c> and releases
    /// it once, on <see cref="Dispose"/> or finalization. A Swift return read without this leaked
    /// the value on every read.
    /// </summary>
    /// <param name="container">The container. When owned, it is consumed and must not be released elsewhere.</param>
    /// <param name="ownsContainer">True when the union adopts the container's <c>+1</c>.</param>
    /// <param name="classBound">
    /// True when the container was widened from a class-bound existential (a class reference in
    /// <c>Payload0</c>, no stored type metadata), whose <c>+1</c> is that reference.
    /// </param>
    public ExistentialUnion(ExistentialContainer1 container, bool ownsContainer, bool classBound)
    {
        _container = container;
        _ownsContainer = ownsContainer;
        _classBound = classBound;
        if (!ownsContainer)
            GC.SuppressFinalize(this);
        else if (!classBound)
            // Resolved now so neither release path looks metadata up (the finalizer must not).
            _containerMetadata = TypeMetadata.GetExistentialTypeMetadata(1);
    }

    /// <summary>
    /// Creates an owning union over a copy of a container Swift still owns (a borrowed argument, an
    /// element read in place), so the union stays valid after the source is gone.
    /// </summary>
    /// <param name="container">The borrowed container. Its ownership is unchanged.</param>
    /// <param name="classBound">True for a container widened from a class-bound existential.</param>
    public static unsafe ExistentialUnion FromBorrowed(ExistentialContainer1 container, bool classBound)
    {
        if (classBound)
        {
            Arc.UnknownObjectRetain(container.Payload0);
            return new ExistentialUnion(container, ownsContainer: true, classBound: true);
        }
        ExistentialContainer1 copy = default;
        Swift.Runtime.InteropServices.SwiftMarshal.CopyWireBufferRetains(
            (IntPtr)(&copy), (IntPtr)(&container), TypeMetadata.GetExistentialTypeMetadata(1));
        return new ExistentialUnion(copy, ownsContainer: true, classBound: false);
    }

    /// <summary>Whether an owned value has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _released) != 0;

    /// <summary>
    /// Gets the underlying existential container for parameter marshalling (borrowed).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The owned value has been released.</exception>
    public ExistentialContainer1 GetExistentialContainer()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _container;
    }

    /// <summary>Releases an owned value. No-op for a borrowed one; safe to call more than once.</summary>
    public void Dispose()
    {
        Release(finalizing: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases an owned value that was never disposed.</summary>
    ~ExistentialUnion()
    {
        Release(finalizing: true);
    }

    private unsafe void Release(bool finalizing)
    {
        if (!_ownsContainer || Interlocked.Exchange(ref _released, 1) != 0)
            return;
        if (_classBound)
        {
            if (finalizing)
                Arc.UnknownObjectReleaseFinalizerSafe(_container.Payload0);
            else
                Arc.UnknownObjectRelease(_container.Payload0);
            return;
        }
        fixed (ExistentialContainer1* pinned = &_container)
        {
            if (finalizing)
                Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe((IntPtr)pinned, _containerMetadata);
            else
                Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)pinned, _containerMetadata);
        }
    }

    /// <summary>
    /// Gets the Swift type metadata for the value inside the container.
    /// This identifies which concrete conformer is stored.
    /// </summary>
    /// <exception cref="ObjectDisposedException">A class-bound owned value has been released.</exception>
    public TypeMetadata ObjectMetadata
    {
        get
        {
            if (!_classBound)
                return _container.ObjectMetadata;
            // A container widened from a class-bound existential stores no metadata word: the
            // dynamic type is the class instance's own, which may only be read while it is held.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var metadata = swift_getObjectType(_container.Payload0);
            // An owning union's finalizer releases the instance; hold it until the read is done.
            GC.KeepAlive(this);
            return metadata;
        }
    }

    /// <summary>
    /// Attempts to cast the existential value to a specific concrete conformer type.
    /// Compares the container's type metadata against the expected conformer's metadata.
    /// If they match, creates a new instance of <typeparamref name="T"/> from the container payload.
    /// </summary>
    /// <typeparam name="T">The concrete conformer type (must implement ISwiftObject).</typeparam>
    /// <returns>An instance of <typeparamref name="T"/> if the container holds that type; null otherwise.</returns>
    /// <exception cref="ObjectDisposedException">The owned value has been released.</exception>
    public T? As<T>() where T : class, ISwiftObject
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var expected = SwiftObjectHelper<T>.GetTypeMetadata();
        if (!ObjectMetadata.Equals(expected))
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

            // Both branches below resolve a BORROWED interior pointer: the existential (its inline
            // payload words, or the swift_allocBox heap box) retains ownership of the value's +1.
            // Route that borrowed source through SwiftMarshal.ExtractCopiedValue<T>, which copies out
            // an INDEPENDENT value the returned wrapper owns and disposes — InitializeWithCopy (+1)
            // for reference-backed struct conformers, an ARC retain of the instance pointer for true
            // Swift classes — instead of handing the borrowed pointer straight to NewFromPayload.
            // NewFromPayload's ownership contract VARIES by conformer shape, and for the resilient
            // (non-@frozen) struct shape it ADOPTS the pointer: disposing such a wrapper ran a
            // value-witness Destroy over still-borrowed storage and then NativeMemory.Free'd a box
            // interior (or a stack local, on the inline branch) the runtime never allocated — an
            // invalid free / use-after-free. ExtractCopiedValue makes the read uniformly safe across
            // adopt / copy / move struct shapes AND the true-class shape (where the payload word is
            // the instance pointer, not a value buffer), which a bitwise copy would mishandle.
            if (size <= ExistentialContainerFactory.MaxInlinePayloadSize && !isNonInline)
            {
                // Inline storage: value data is in payload0/payload1/payload2 (payload0 offset = 0).
                // containerCopy must stay alive across the extraction below — keep the call in-scope.
                var containerCopy = _container;
                var inlineValue = Swift.Runtime.InteropServices.SwiftMarshal.ExtractCopiedValue<T>(
                    Unsafe.AsPointer(ref containerCopy), vwt->Size);
                // The copy above borrows the payload this union owns; an owning union's finalizer
                // must not release it before the extracted value holds its own reference.
                GC.KeepAlive(this);
                return inlineValue;
            }
            else
            {
                // Out-of-line storage: payload0 is a swift_allocBox heap object (refcount header +
                // value), the exact inverse of MarshalPayload's write path, which stores boxPair.HeapObject
                // — NOT boxPair.Buffer — in payload0. Project past the box header to the value buffer before
                // reading it; the box keeps its own +1, so this is a borrowed read.
                var valuePtr = swift_projectBox(_container.Payload0);
                var boxedValue = Swift.Runtime.InteropServices.SwiftMarshal.ExtractCopiedValue<T>(
                    (void*)valuePtr, vwt->Size);
                GC.KeepAlive(this);
                return boxedValue;
            }
        }
    }

    /// <summary>
    /// Projects a Swift box (the heap object stored in an out-of-line existential payload by
    /// <c>swift_allocBox</c>) to the address of the value it holds — the inverse of
    /// <see cref="ExistentialContainerFactory"/>'s <c>swift_allocBox</c> write path. Read-only:
    /// does not retain or release the box.
    /// </summary>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern TypeMetadata swift_getObjectType(IntPtr instance);

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
