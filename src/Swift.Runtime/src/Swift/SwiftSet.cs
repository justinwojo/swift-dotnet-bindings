// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift hashable protocol.
/// </summary>
public interface ISwiftHashable { }

/// <summary>
/// Marker interface for Swift's <c>Equatable</c> protocol. The protocol has a <c>Self</c>
/// requirement so it cannot be satisfied by a concrete C# interface; the generator emits
/// the PWT via the descriptor-symbol path and drops the constraint from C# where clauses.
/// This marker exists so the XML database entry has a valid managed type to point at.
/// </summary>
public interface ISwiftEquatable { }

/// <summary>
/// Marker interface for Swift's <c>Comparable</c> protocol. Same rationale as
/// <see cref="ISwiftEquatable"/> — present for the XML managedTypeName reference; not used
/// as a real C# generic constraint.
/// </summary>
public interface ISwiftComparable { }

/// <summary>
/// Marker interface for Swift's <c>Decodable</c> protocol. PAT-like (associated decoder
/// context); not projected as a usable C# interface, present only as a managed type
/// reference for the XML database entry.
/// </summary>
public interface ISwiftDecodable { }

/// <summary>
/// Marker interface for Swift's <c>Encodable</c> protocol. Same rationale as
/// <see cref="ISwiftDecodable"/>.
/// </summary>
public interface ISwiftEncodable { }

/// <summary>
/// Marker interface for Swift's <c>Sendable</c> protocol. <c>Sendable</c> is a compile-time
/// marker protocol with no witness table — it has no runtime members and produces no
/// conformance descriptor in TBD. The XML database entry exists so that any type that lists
/// <c>Sendable</c> in its conformance list (notably every actor type and every value type that
/// crosses a concurrency boundary) can resolve a TypeRecord instead of being treated as
/// referencing an unknown protocol. The generator never emits a concrete conformance because
/// <c>IsWellKnownRuntimeProtocol</c>-style gates filter it out before
/// <c>GetImplementedInterfaces</c> / dictionary entry construction.
/// </summary>
public interface ISwiftSendable { }

/// <summary>
/// Marker interface for Swift's <c>Copyable</c> protocol. Marker protocol — no witness table,
/// no descriptor. Present for typedb resolution only; see <see cref="ISwiftSendable"/>.
/// </summary>
public interface ISwiftCopyable { }

/// <summary>
/// Marker interface for Swift's <c>Escapable</c> protocol. Marker protocol — no witness table,
/// no descriptor. Present for typedb resolution only; see <see cref="ISwiftSendable"/>.
/// </summary>
public interface ISwiftEscapable { }

/// <summary>
/// Marker interface for Swift's <c>SendableMetatype</c> protocol. Marker protocol — no
/// witness table, no descriptor. Present for typedb resolution only; see
/// <see cref="ISwiftSendable"/>.
/// </summary>
public interface ISwiftSendableMetatype { }

/// <summary>
/// Marker interface for Swift's <c>_Concurrency.Actor</c> protocol. Actor types all
/// implicitly conform to <c>Actor</c>; the type database entry lets the parser resolve
/// the conformance descriptor without falling off the well-known-protocol path.
/// Not user-implementable — actors are constructed via Swift, not declared in C#.
/// </summary>
public interface ISwiftActor { }

/// <summary>
/// Marker interface for Swift's <c>_Concurrency.GlobalActor</c> protocol. <c>GlobalActor</c>
/// has an associated type (<c>ActorType</c>), so it never produces a usable C# interface;
/// the typedb entry exists purely for conformance-descriptor lookup on global-actor-isolated
/// types like <c>@MainActor</c>.
/// </summary>
public interface ISwiftGlobalActor { }

/// <summary>
/// Represents a Swift set.
/// </summary>
/// <typeparam name="Element">The element type contained in the set.</typeparam>
public class SwiftSet<Element> : ISwiftObject, ISwiftStruct, ICollection<Element>, IReadOnlyCollection<Element>, IReadOnlySet<Element>, IDisposable
{
    // Lazy initialization to avoid calling Swift runtime during static construction.
    // This prevents crashes when Element is an existential container type, where
    // swift_getExistentialTypeMetadata called from .cctor triggers a Mono JIT/async assertion.
    private static TypeMetadata? _cachedElementMetadata;
    private static nuint? _cachedElementSize;

    private static TypeMetadata CachedElementTypeMetadata
    {
        get
        {
            _cachedElementMetadata ??= TypeMetadata.GetTypeMetadataOrThrow<Element>();
            return _cachedElementMetadata.Value;
        }
    }

    private static nuint ElementSize
    {
        get
        {
            _cachedElementSize ??= CachedElementTypeMetadata.Size;
            return _cachedElementSize.Value;
        }
    }

    private SwiftSafeHandle<SwiftSet<Element>> _payload;
    private bool _disposed;

    public SwiftSafeHandle<SwiftSet<Element>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<IntPtr>(_payload); }
    }

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftSet()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftCollection), "$sShyxGSlsMc" }
        };

        // On NativeAOT, eagerly populate the type metadata cache during type init.
        // Reflection on explicit interface implementations of generic types may fail
        // on NativeAOT, so callers going through TypeMetadata.GetTypeMetadataOrThrow<
        // SwiftSet<Element>>() (e.g., SwiftOptional<SwiftSet<...>>.cctor) cannot
        // resolve metadata via reflection. Direct dispatch via SwiftObjectHelper<T>
        // populates the cache without reflection.
        // On Mono, skip this — calling Swift runtime during static construction can
        // trigger JIT assertions when Element is an existential container type.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            TryEagerInitialize();
        }
    }

    /// <summary>
    /// Attempts eager initialization of metadata and factory registration for NativeAOT.
    /// Mirrors SwiftArray.TryEagerInitialize. Returns true on success, false if it
    /// fell back to lazy init (e.g., when Element metadata isn't yet available).
    /// </summary>
    internal static bool TryEagerInitialize()
    {
        try
        {
            NativeAotInitialize();
            return true;
        }
        catch (Exception)
        {
            // Element metadata may be unavailable during type init for certain types
            // (e.g., ExistentialContainer types require protocol descriptor pointers).
            // Fall back to lazy initialization — metadata will be fetched on first use.
            System.Diagnostics.Debug.WriteLine(
                $"SwiftSet<{typeof(Element).Name}>: NativeAotInitialize skipped, using lazy init");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotInitialize()
    {
        // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata():
        // - Registers NewFromPayload factory in NewFromPayloadDispatcher
        // - Caches metadata in TypeMetadata.Cache
        var _ = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        // Uses HashableConformanceRegistry for NativeAOT safety — avoids MakeGenericType
        // reflection for unconstrained Element types (e.g., SwiftSet<IntPtr>).
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<Element>();
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftSet<Element>), _ => SwiftSetPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, ElementTypeMetadata, witnessTable));
    }

    // Use cached version to avoid static constructor issues
    static TypeMetadata ElementTypeMetadata => CachedElementTypeMetadata;

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftSet<Element>(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Copy;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure the payload is valid before making copy
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type.
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
        {
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type SwiftSet and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Constructs a new SwiftSet from the given handle.
    /// </summary>
    unsafe SwiftSet(IntPtr handle)
    {
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftSet<Element>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new empty SwiftSet.
    /// </summary>
    public unsafe SwiftSet()
    {
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<Element>();
        var result = SwiftSetPInvokes.Init(ElementTypeMetadata, witnessTable);

        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        *(IntPtr*)bufferPtr = result;
        _payload = new SwiftSafeHandle<SwiftSet<Element>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftSet from an enumerable source.
    /// </summary>
    /// <param name="source">The source enumerable to copy elements from.</param>
    public SwiftSet(IEnumerable<Element> source) : this()
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        AddRange(source);
    }

    /// <summary>
    /// Gets the number of elements in the set.
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<Element>();
            int result = (int)SwiftSetPInvokes.Count(disposable.Buffer, ElementTypeMetadata, witnessTable);
            return result;
        }
    }

    /// <summary>
    /// Determines whether the set contains the specified element.
    /// </summary>
    /// <param name="element">The element to locate in the set.</param>
    /// <returns><c>true</c> if the set contains the element; otherwise, <c>false</c>.</returns>
    public unsafe bool Contains(Element element)
    {
        ThrowIfDisposed();
        using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<Element>();

        Span<byte> span = stackalloc byte[(int)ElementSize];
        SwiftMarshal.MarshalToSwift(element, ref span);
        IntPtr elementPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

        byte result = SwiftSetPInvokes.Contains(elementPayload, disposable.Buffer, ElementTypeMetadata, witnessTable);
        return result != 0;
    }

    /// <summary>
    /// Inserts the given element into the set if it is not already present.
    /// </summary>
    /// <param name="element">The element to insert.</param>
    /// <returns><c>true</c> if the element was inserted (was not already present);
    /// <c>false</c> if the element was already in the set.</returns>
    public unsafe bool Add(Element element)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            return InsertUnsafe(element, metadata, _payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Bulk-add every element from <paramref name="source"/> under a single payload
    /// SafeHandle scope. Replaces N <c>DangerousAddRef</c>/<c>Release</c> pairs (one
    /// per <see cref="Add(Element)"/>) with one for large inserts; the per-element
    /// marshal + Swift-stdlib P/Invoke is unchanged.
    /// </summary>
    private unsafe void AddRange(IEnumerable<Element> source)
    {
        if (source is ICollection<Element> collection && collection.Count == 0)
            return;

        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            IntPtr handle = _payload.DangerousGetHandle();
            foreach (var item in source)
            {
                InsertUnsafe(item, metadata, handle);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Issues a single <c>insert(_:)</c> call without acquiring the payload
    /// SafeHandle. The caller must hold a successful <c>DangerousAddRef</c> scope
    /// across the call.
    /// </summary>
    /// <remarks>
    /// No arm here calls Swift stdlib's `Set.insert` through a CallConvSwift
    /// P/Invoke. Its ABI returns `(inserted: Bool, memberAfterInsert: Element)` —
    /// a (direct Bool, @out via x0) tuple-return shape that Mono's CallConvSwift
    /// trampoline mishandles on iOS Simulator: the call either SIGABRTs on the
    /// managed-to-native transition, or appears to succeed while a stack address
    /// from the trampoline's scratch frame is written into the caller's `self`
    /// slot, giving a garbage `Count` and a SIGSEGV on a later insert or on the
    /// next VWT Destroy. `Dictionary.updateValue` (pure `@out` via
    /// x8/SwiftIndirectResult) does NOT exhibit the same corruption — the bug is
    /// shape-specific.
    ///
    /// Two kinds of workaround are in play, and they differ in ownership:
    /// <list type="bullet">
    /// <item>Int64/Int/String route through Swift `@_cdecl` wrappers
    /// (<c>SBW_SetInt64_Insert</c>, <c>SBW_SetInt_Insert</c>,
    /// <c>SBW_SetString_Insert</c>), whose Swift bodies `.move()` out of the
    /// element buffer.</item>
    /// <item>Every other element type routes through the C-side swiftcall shim
    /// <c>SBW_Set_Insert</c>, which forwards to the stdlib symbol unchanged — so
    /// the stdlib's own ownership contract applies verbatim (element consumed,
    /// `memberAfterInsert` destroyed by the caller).</item>
    /// </list>
    /// </remarks>
    private static unsafe bool InsertUnsafe(Element element, TypeMetadata metadata, IntPtr handle)
    {
        // C# `long` ↔ Swift.Int64 (`$ss5Int64V`). Routed through Set<Int64> wrapper.
        if (typeof(Element) == typeof(long))
        {
            long longElement = Unsafe.As<Element, long>(ref element);
            long outMember = 0;
            byte inserted = SwiftSetCdeclWrappers.SetInt64Insert(handle, longElement, (IntPtr)(&outMember));
            // outMember is a plain Int64 — no VWT Destroy needed.
            return inserted != 0;
        }

        // C# `nint` (IntPtr) ↔ Swift.Int (`$sSi`). Routed through Set<Int> wrapper.
        // Different generic instantiation than Set<Int64> despite identical layout
        // on 64-bit platforms (per HashableConformanceRegistry mapping).
        if (typeof(Element) == typeof(nint))
        {
            nint nintElement = Unsafe.As<Element, nint>(ref element);
            nint outMember = 0;
            byte inserted = SwiftSetCdeclWrappers.SetIntInsert(handle, nintElement, (IntPtr)(&outMember));
            // outMember is a plain Int — no VWT Destroy needed.
            return inserted != 0;
        }

        if (typeof(Element) == typeof(SwiftString))
        {
            // String is 16 bytes / 2 words on arm64.
            Span<byte> span = stackalloc byte[(int)ElementSize];
            SwiftMarshal.MarshalToSwift(element, ref span);
            IntPtr elementPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

            Span<byte> outSpan = stackalloc byte[(int)ElementSize];
            outSpan.Clear();
            void* outMemberBuffer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(outSpan));

            byte inserted = SwiftSetCdeclWrappers.SetStringInsert(
                handle, elementPayload, (IntPtr)outMemberBuffer);

            // The Swift wrapper consumed `elementPayload` via .move() — its +1 retain
            // was transferred into the Set (or released if the element was already
            // present). Do NOT destroy `span`. The wrapper wrote `memberAfterInsert`
            // into `outMemberBuffer` with a +1 retain — destroy it before discarding.
            CachedElementTypeMetadata.ValueWitnessTable->Destroy(outMemberBuffer, CachedElementTypeMetadata);
            return inserted != 0;
        }

        // General path for element types without a typed Swift `@_cdecl`
        // wrapper — arbitrary structs, classes, enums. Goes through the C-side
        // `SBW_Set_Insert` swiftcall shim rather than the raw CallConvSwift
        // P/Invoke: the (Bool direct, @out via x0) tuple-return shape corrupts
        // Mono's thread state on the iOS Simulator, so the raw call either
        // SIGABRTs on the managed-to-native transition or leaves the Set's
        // `self` slot holding a trampoline scratch address (garbage Count, then
        // a SIGSEGV on a later insert or on release). The shim keeps the
        // managed boundary plain-Cdecl and lets LLVM swiftcc do the lowering.
        //
        // Ownership matches what swiftc emits at a `Set.insert` call site:
        // `elementPayload` is marshalled +1 and CONSUMED by the `@in` insert,
        // so it must not be destroyed here; `outMemberBuffer` receives
        // `memberAfterInsert` at +1 and is destroyed through the element VWT
        // before being freed.
        {
            Span<byte> span = stackalloc byte[(int)ElementSize];
            SwiftMarshal.MarshalToSwift(element, ref span);
            IntPtr elementPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

            void* outMemberBuffer = NativeMemory.Alloc(ElementSize);
            try
            {
                byte inserted = SwiftCollectionCdeclWrappers.SetInsert(
                    (IntPtr)outMemberBuffer,
                    elementPayload,
                    metadata,
                    handle);

                CachedElementTypeMetadata.ValueWitnessTable->Destroy(outMemberBuffer, CachedElementTypeMetadata);
                return inserted != 0;
            }
            finally
            {
                NativeMemory.Free(outMemberBuffer);
            }
        }
    }

    /// <summary>
    /// Removes the specified element from the set.
    /// </summary>
    /// <param name="element">The element to remove.</param>
    /// <returns><c>true</c> if the element was removed; <c>false</c> if the element was not in the set.</returns>
    public unsafe bool Remove(Element element)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            Span<byte> span = stackalloc byte[(int)ElementSize];
            SwiftMarshal.MarshalToSwift(element, ref span);
            IntPtr elementPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

            // Remove returns Optional<Element> via SwiftIndirectResult (x8)
            var optionalMetadata = SwiftObjectHelper<SwiftOptional<Element>>.GetTypeMetadata();
            void* resultPayload = NativeMemory.Alloc(optionalMetadata.Size);
            try
            {
                // Cdecl-wrapped to bypass the Mac Catalyst-x64 workload Mono
                // CallConvSwift trampoline; see SwiftCollectionCdeclWrappers.
                SwiftCollectionCdeclWrappers.SetRemove(
                    (IntPtr)resultPayload,
                    elementPayload,
                    metadata,
                    _payload.DangerousGetHandle());

                var tag = (SwiftOptionalCases)optionalMetadata.ValueWitnessTable->GetEnumTag(
                    (byte*)resultPayload, optionalMetadata);

                // Destroy the optional result to clean up any ref-counted payload
                optionalMetadata.ValueWitnessTable->Destroy(resultPayload, optionalMetadata);

                return tag != SwiftOptionalCases.None;
            }
            finally
            {
                NativeMemory.Free(resultPayload);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Removes all elements from the set.
    /// </summary>
    public unsafe void RemoveAll()
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftSet<Element>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            SwiftSetPInvokes.RemoveAll(
                0, // keepingCapacity: false
                metadata,
                new SwiftSelf((void*)_payload.DangerousGetHandle()));
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through the set.
    /// Uses Swift's Set.makeIterator() and Set.Iterator.next() P/Invokes.
    /// </summary>
    public IEnumerator<Element> GetEnumerator()
    {
        ThrowIfDisposed();
        // Snapshot all elements into a list (unsafe code can't coexist with yield return)
        var elements = CollectElements();
        return elements.GetEnumerator();
    }

    /// <summary>
    /// Collects all elements from the Swift set using the iterator P/Invokes.
    /// Separated from GetEnumerator because yield return cannot be used in unsafe methods.
    /// </summary>
    private unsafe List<Element> CollectElements()
    {
        var result = new List<Element>();
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<Element>();

        // Get the metadata for Set.Iterator<Element>
        var iteratorMetadata = SwiftSetPInvokes.PInvoke_getIteratorMetadata(
            TypeMetadataRequest.Complete, ElementTypeMetadata, witnessTable);

        // Get Optional<Element> metadata for proper .none detection via GetEnumTag.
        var optionalElementMetadata = PInvokesForSwiftOptional._MetadataAccessor(
            TypeMetadataRequest.Complete, ElementTypeMetadata);

        // Allocate the iterator buffer
        void* iteratorBuffer = NativeMemory.AllocZeroed(iteratorMetadata.Size);
        try
        {
            // Call Set.makeIterator() — writes the iterator into the indirect result.
            // Arc.Retain the set storage before calling makeIterator. The iterator
            // takes ownership of the storage reference passed via the set value
            // parameter. Without this retain, the iterator's VWT Destroy would over-release
            // the set's storage, causing a crash on set.Dispose().
            using (PayloadBuffer<IntPtr> disposable = PayloadBuffer)
            {
                Arc.Retain((IntPtr)disposable.Buffer);
                SwiftSetPInvokes.MakeIterator(
                    new SwiftIndirectResult(iteratorBuffer),
                    disposable.Buffer,
                    ElementTypeMetadata,
                    witnessTable);
            }

            void* nextResultBuffer = NativeMemory.Alloc(optionalElementMetadata.Size);
            // Track whether the current buffer's element slot has been consumed.
            // MarshalMovedValueFromSlot consumes the slot's +1 atomically — class deref-transfer,
            // or ADOPT/COPY copy-out-then-value-witness-Destroy — so on success the buffer is freed
            // raw and a whole-buffer Destroy would double-release. The helper is atomic (a throw
            // leaves the slot intact), so if it (or IteratorNext) throws before consumption the
            // unconsumed result must be Destroyed to avoid leaking ref-counted values.
            bool resultConsumed = true; // starts true (buffer is uninitialized)
            try
            {
                while (true)
                {
                    // Call Set.Iterator.next() — mutates the iterator in-place.
                    // Cdecl-wrapped to bypass the Mac Catalyst-x64 workload Mono
                    // CallConvSwift trampoline; see SwiftCollectionCdeclWrappers.
                    SwiftCollectionCdeclWrappers.SetIteratorNext(
                        (IntPtr)nextResultBuffer,
                        iteratorMetadata,
                        (IntPtr)iteratorBuffer);
                    resultConsumed = false; // buffer now holds an initialized Optional value

                    // Use VWT GetEnumTag to check Optional.none — the only correct way
                    // to detect .none for all type combinations (pointer, value, existential).
                    var tag = (SwiftOptionalCases)optionalElementMetadata.ValueWitnessTable->GetEnumTag(
                        (byte*)nextResultBuffer, optionalElementMetadata);
                    if (tag == SwiftOptionalCases.None)
                    {
                        // .none has no ref-counted payload to leak, but destroy for correctness
                        optionalElementMetadata.ValueWitnessTable->Destroy(nextResultBuffer, optionalElementMetadata);
                        resultConsumed = true;
                        break;
                    }

                    // Move the +1 the iterator wrote into the buffer out into the marshalled object.
                    // The helper consumes the slot itself (class deref-transfer, or ADOPT/COPY
                    // copy-out + value-witness-Destroy of the slot), so the buffer is freed raw with
                    // no whole-buffer Destroy below; a ref-containing non-class member would otherwise
                    // leak (COPY) or alias the freed buffer (ADOPT).
                    Element elem = SwiftMarshal.MarshalMovedValueFromSlot<Element>(nextResultBuffer, ElementTypeMetadata);
                    resultConsumed = true; // slot consumed by the helper

                    result.Add(elem);
                }
            }
            finally
            {
                // Destroy unconsumed result (exception between IteratorNext and MarshalFromSwift)
                if (!resultConsumed)
                    optionalElementMetadata.ValueWitnessTable->Destroy(nextResultBuffer, optionalElementMetadata);
                NativeMemory.Free(nextResultBuffer);
            }
        }
        finally
        {
            // Destroy the iterator — this releases the retained storage reference
            iteratorMetadata.ValueWitnessTable->Destroy(iteratorBuffer, iteratorMetadata);
            NativeMemory.Free(iteratorBuffer);
        }

        return result;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #region ICollection<Element> explicit implementation

    /// <summary>
    /// Gets a value indicating whether the set is read-only.
    /// </summary>
    bool ICollection<Element>.IsReadOnly => false;

    /// <summary>
    /// Adds the specified element to the set (ICollection interface).
    /// </summary>
    void ICollection<Element>.Add(Element item) => Add(item);

    /// <summary>
    /// Removes all elements from the set (ICollection interface).
    /// </summary>
    void ICollection<Element>.Clear() => RemoveAll();

    /// <summary>
    /// Copies the elements of the set to an array.
    /// </summary>
    void ICollection<Element>.CopyTo(Element[] array, int arrayIndex)
    {
        if (array == null) throw new ArgumentNullException(nameof(array));
        var elements = CollectElements();
        if (arrayIndex < 0 || arrayIndex + elements.Count > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        for (int i = 0; i < elements.Count; i++)
            array[arrayIndex + i] = elements[i];
    }

    #endregion

    #region IReadOnlySet<Element> implementation

    /// <summary>
    /// Determines whether the current set is a proper subset of a specified collection.
    /// </summary>
    public bool IsProperSubsetOf(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        var otherSet = new HashSet<Element>(other);
        if (Count >= otherSet.Count) return false;
        foreach (var element in this)
        {
            if (!otherSet.Contains(element))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether the current set is a proper superset of a specified collection.
    /// </summary>
    public bool IsProperSupersetOf(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        var otherSet = new HashSet<Element>(other);
        if (Count <= otherSet.Count) return false;
        foreach (var element in otherSet)
        {
            if (!Contains(element))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether the current set is a subset of a specified collection.
    /// </summary>
    public bool IsSubsetOf(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        var otherSet = new HashSet<Element>(other);
        foreach (var element in this)
        {
            if (!otherSet.Contains(element))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether the current set is a superset of a specified collection.
    /// </summary>
    public bool IsSupersetOf(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        foreach (var element in other)
        {
            if (!Contains(element))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether the current set overlaps with the specified collection.
    /// </summary>
    public bool Overlaps(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        foreach (var element in other)
        {
            if (Contains(element))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Determines whether the current set and a specified collection contain the same elements.
    /// </summary>
    public bool SetEquals(IEnumerable<Element> other)
    {
        ThrowIfDisposed();
        var otherSet = new HashSet<Element>(other);
        if (Count != otherSet.Count) return false;
        foreach (var element in this)
        {
            if (!otherSet.Contains(element))
                return false;
        }
        return true;
    }

    #endregion

    /// <summary>
    /// Creates a new SwiftSet from an enumerable source.
    /// </summary>
    /// <param name="source">The source enumerable to copy elements from.</param>
    /// <returns>A new SwiftSet containing the elements from the source.</returns>
    public static SwiftSet<Element> FromEnumerable(IEnumerable<Element> source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        var set = new SwiftSet<Element>();
        set.AddRange(source);
        return set;
    }

    /// <summary>
    /// Copies the elements to a new .NET array.
    /// </summary>
    public Element[] ToArray()
    {
        ThrowIfDisposed();
        var elements = CollectElements();
        return elements.ToArray();
    }

    /// <summary>
    /// Copies the elements to a new List.
    /// </summary>
    public List<Element> ToList()
    {
        ThrowIfDisposed();
        return CollectElements();
    }

    /// <summary>
    /// Returns a string representation of the set.
    /// </summary>
    public override string ToString()
    {
        ThrowIfDisposed();
        return $"SwiftSet<{typeof(Element).Name}>[{Count}]";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _payload?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

// Direct CallConvSwift bindings for Swift stdlib `Set` operations.
//
// Three of these — `Insert`, `Remove` and `IteratorNext` — are declared but not
// called: their ABI shapes are mishandled by a Mono CallConvSwift trampoline, so
// live dispatch goes through the C-side cdecl shims in
// `SwiftCollectionCdeclWrappers` instead. The declarations stay because they are
// the executable record of each op's register layout, kept next to the call
// sites that consume them; the shim's C declaration must agree with them.
// Adding a caller back to one of the three re-introduces the crash.
internal static class SwiftSetPInvokes
{
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sShMa")]
    public static extern TypeMetadata PInvoke_getMetadata(TypeMetadataRequest request, TypeMetadata typeMetadata, ProtocolWitnessTable witnessTable);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sS2hyxGycfC")]
    public static extern IntPtr Init(TypeMetadata elementTypeMetadata, ProtocolWitnessTable witnessTable);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh5countSivg")]
    public static extern nint Count(IntPtr handle, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);

    // Set.contains(_:): $sSh8containsySbxF
    // Non-mutating: self as handle, generic context as element metadata + witness table
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh8containsySbxF")]
    public static extern byte Contains(IntPtr element, IntPtr handle, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);

    // Set.insert(_:): $sSh6insertySb8inserted_x17memberAfterInserttxnF
    // SIL: (@in Element, @inout Set<Element>) -> (Bool, @out Element)
    // The @out Element in the return tuple becomes a regular x0 parameter (NOT SwiftIndirectResult/x8).
    // Bool is returned directly in x0. Generic context = full Set metadata.
    // ARM64: x0=outMemberBuffer, x1=element, x2=setMetadata, x20=self, return byte
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh6insertySb8inserted_x17memberAfterInserttxnF")]
    public static extern byte Insert(IntPtr outMemberBuffer, IntPtr element, TypeMetadata setMetadata, SwiftSelf self);

    // Set.remove(_:): $sSh6removeyxSgxF
    // SIL: (@in_guaranteed Element, @inout Set<Element>) -> @out Optional<Element>
    // Pure @out return uses SwiftIndirectResult (x8). Generic context = full Set metadata.
    // ARM64: x0=element, x1=setMetadata, x8=result, x20=self
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh6removeyxSgxF")]
    public static extern void Remove(SwiftIndirectResult result, IntPtr element, TypeMetadata setMetadata, SwiftSelf self);

    // Set.removeAll(keepingCapacity:): $sSh9removeAll15keepingCapacityySb_tF
    // SIL: (Bool, @inout Set<Element>) -> ()
    // Void return, direct Bool parameter. Generic context = full Set metadata.
    // ARM64: x0=keepCapacity, x1=setMetadata, x20=self
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh9removeAll15keepingCapacityySb_tF")]
    public static extern void RemoveAll(byte keepCapacity, TypeMetadata setMetadata, SwiftSelf self);

    // Set.Iterator metadata accessor: $sSh8IteratorVMa
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh8IteratorVMa")]
    public static extern TypeMetadata PInvoke_getIteratorMetadata(TypeMetadataRequest request, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);

    // Set.makeIterator(): $sSh12makeIteratorSh0B0Vyx_GyF
    // SIL: (@owned Set<Element>) -> @owned Set<Element>.Iterator
    // Non-mutating: self as handle, generic context as element metadata + witness table
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh12makeIteratorSh0B0Vyx_GyF")]
    public static extern void MakeIterator(SwiftIndirectResult result, IntPtr handle, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);

    // Set.Iterator.next(): $sSh8IteratorV4nextxSgyF
    // Mutating on Iterator: returns Optional<Element> via SwiftIndirectResult
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh8IteratorV4nextxSgyF")]
    public static extern void IteratorNext(SwiftIndirectResult result, TypeMetadata iteratorMetadata, SwiftSelf self);
}

/// <summary>
/// Cdecl wrappers for <see cref="Swift.SwiftSet{Element}"/> operations whose
/// CallConvSwift P/Invokes are mishandled by Mono on iOS Simulator. See
/// <see cref="Swift.SwiftSet{Element}"/>'s <c>InsertUnsafe</c> for context on
/// the (Bool direct, @out via x0) tuple-return shape this works around.
/// </summary>
internal static class SwiftSetCdeclWrappers
{
    /// <summary>
    /// Inserts an Int64 into a Set&lt;Int64&gt;. Wraps Swift's
    /// <c>set.insert(element)</c> behind a Cdecl boundary. Used for C# <c>long</c>
    /// (Swift.Int64). See <see cref="SetIntInsert"/> for the Swift.Int (C# nint) variant.
    /// </summary>
    /// <param name="setHandle">Pointer to the Set's 8-byte storage slot.</param>
    /// <param name="element">The Int64 value to insert.</param>
    /// <param name="outMember">Pointer to a caller-owned 8-byte buffer for memberAfterInsert.</param>
    /// <returns>1 if inserted; 0 if already present.</returns>
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_SetInt64_Insert", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern byte SetInt64Insert(IntPtr setHandle, long element, IntPtr outMember);

    /// <summary>
    /// Inserts an Int into a Set&lt;Int&gt;. Wraps Swift's
    /// <c>set.insert(element)</c> behind a Cdecl boundary. Used for C# <c>nint</c>
    /// (Swift.Int). See <see cref="SetInt64Insert"/> for the Swift.Int64 (C# long) variant.
    /// </summary>
    /// <param name="setHandle">Pointer to the Set's 8-byte storage slot.</param>
    /// <param name="element">The Int (nint-sized) value to insert.</param>
    /// <param name="outMember">Pointer to a caller-owned 8-byte buffer for memberAfterInsert.</param>
    /// <returns>1 if inserted; 0 if already present.</returns>
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_SetInt_Insert", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern byte SetIntInsert(IntPtr setHandle, nint element, IntPtr outMember);

    /// <summary>
    /// Inserts a String into a Set&lt;String&gt;. Wraps Swift's
    /// <c>set.insert(element)</c> behind a Cdecl boundary. The wrapper consumes
    /// (moves out of) <paramref name="elementBuffer"/> — caller must NOT destroy
    /// the element buffer afterwards.
    /// </summary>
    /// <param name="setHandle">Pointer to the Set's 8-byte storage slot.</param>
    /// <param name="elementBuffer">Pointer to a 16-byte Swift.String raw representation. Consumed.</param>
    /// <param name="outMember">Pointer to a caller-owned 16-byte buffer for memberAfterInsert; caller destroys via String VWT.</param>
    /// <returns>1 if inserted; 0 if already present.</returns>
    [DllImport("SwiftBindingsRuntime", EntryPoint = "SBW_SetString_Insert", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern byte SetStringInsert(IntPtr setHandle, IntPtr elementBuffer, IntPtr outMember);
}
