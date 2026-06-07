// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Swift.Runtime.InteropServices;

#nullable enable

/// <summary>
/// Registry of NewFromPayload factory delegates, populated from constrained code paths
/// (SwiftObjectHelper&lt;T&gt;, MarshalFromSwiftObject&lt;T&gt;) and consumed from unconstrained
/// code paths (MarshalFromSwift&lt;T&gt;). This eliminates the need for reflection on NativeAOT
/// when the type has been previously accessed through any constrained API.
/// </summary>
internal static class NewFromPayloadDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<IntPtr, object>> _factories = new();

    /// <summary>
    /// Registers a factory delegate for a type. Called from constrained code paths on NativeAOT.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void Register(Type type, Func<IntPtr, object> factory)
    {
        _factories.TryAdd(type, factory);
    }

    /// <summary>
    /// Attempts to create an object using a previously registered factory.
    /// Returns null if no factory is registered for the given type.
    /// </summary>
    internal static object? TryCreate(Type type, IntPtr handle)
    {
        if (_factories.TryGetValue(type, out var factory))
            return factory(handle);
        return null;
    }
}

/// <summary>
/// Registry of GetProtocolConformanceDescriptor factory delegates, populated from constrained
/// code paths (ProtocolConformanceDescriptorHelper) and consumed from unconstrained code paths
/// (ProtocolConformanceDescriptor.TryGet). Keyed by (Type, ProtocolType) pairs.
/// </summary>
internal static class ConformanceDispatcher
{
    private static readonly ConcurrentDictionary<(Type, Type), Func<ProtocolConformanceDescriptor>> _factories = new();

    /// <summary>
    /// Registers a conformance factory. Called from constrained code paths on NativeAOT.
    /// </summary>
    internal static void Register(Type type, Type protocolType, Func<ProtocolConformanceDescriptor> factory)
    {
        _factories.TryAdd((type, protocolType), factory);
    }

    /// <summary>
    /// Attempts to get a conformance descriptor using a previously registered factory.
    /// Returns null if no factory is registered.
    /// </summary>
    internal static ProtocolConformanceDescriptor? TryGet(Type type, Type protocolType)
    {
        if (_factories.TryGetValue((type, protocolType), out var factory))
            return factory();
        return null;
    }
}

/// <summary>
/// Registry of pre-computed protocol witness tables, populated by generated [ModuleInitializer]
/// code at assembly load time. This eliminates the need for reflection-based
/// ProtocolWitnessTable.GetOrThrow on NativeAOT for SwiftDictionary/SwiftSet operations
/// where the type parameter lacks an ISwiftObject constraint (e.g., TKey in SwiftDictionary).
/// Keyed by (Type, ProtocolType) pairs, maps to the witness table handle (IntPtr).
/// </summary>
internal static class WitnessTableDispatcher
{
    private static readonly ConcurrentDictionary<(Type, Type), ProtocolWitnessTable> _tables = new();

    /// <summary>
    /// Registers a pre-computed witness table for a (type, protocol) pair.
    /// Called from generated [ModuleInitializer] code on NativeAOT.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void Register(Type type, Type protocolType, ProtocolWitnessTable witnessTable)
    {
        _tables.TryAdd((type, protocolType), witnessTable);
    }

    /// <summary>
    /// Attempts to get a pre-registered witness table for the given (type, protocol) pair.
    /// Returns false if no table is registered.
    /// </summary>
    internal static bool TryGet(Type type, Type protocolType, out ProtocolWitnessTable witnessTable)
    {
        return _tables.TryGetValue((type, protocolType), out witnessTable);
    }
}

/// <summary>
/// Represents a class for marshaling data to and from Swift
/// </summary>
public static class SwiftMarshal
{
    /// <summary>
    /// Returns the native allocation size for a Swift type via its type metadata.
    /// Used by generated code for metadata-driven indirect result buffer allocation.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type whose size to query.</typeparam>
    /// <returns>The size in bytes of the Swift type's value representation.</returns>
    public static int GetSwiftTypeSize<T>() where T : ISwiftObject
        => (int)T.GetTypeMetadata().Size;

    /// <summary>
    /// Releases the value-witness retains held by a Swift wire-buffer for a value type
    /// whose <c>NewFromPayload</c> made a <c>NativeMemory.Alloc</c> +
    /// <c>InitializeWithCopy</c> copy of the payload. The carrier itself is not freed —
    /// generated code follows this with the per-module <c>SBW_Free</c> so the allocator
    /// matches the Swift wrapper's <c>UnsafeMutableRawPointer.allocate</c>.
    /// </summary>
    /// <remarks>
    /// <para>Required only for the frozen-with-memory struct shape
    /// (<c>IsFrozenStructProjectedAsClass</c> in the generator): for that shape
    /// <c>WriteNewFromPayloadFrozenStruct</c> emits an <c>InitializeWithCopy</c> from the
    /// wire <c>handle</c> into a fresh <c>NativeMemory.Alloc</c> buffer owned by the
    /// constructed <c>SwiftSafeHandle</c>. The original wire buffer keeps its
    /// <c>+1</c> retains on the heap fields, so without an explicit VWT
    /// <c>Destroy</c> on the source the retained inner allocations leak. Non-frozen
    /// structs and complex enums wrap the wire <c>handle</c> directly into the
    /// SafeHandle and don't need this helper — the SafeHandle's <c>ReleaseHandle</c>
    /// runs the destroy itself.</para>
    /// <para>If type metadata is unavailable (e.g. mock types in unit tests),
    /// the destroy step is skipped silently — the helper never throws.</para>
    /// </remarks>
    /// <typeparam name="T">The Swift wrapper type whose value occupies the buffer.</typeparam>
    /// <param name="buffer">The wire buffer pointer to destroy. <c>IntPtr.Zero</c> is a no-op.</param>
    public static unsafe void DestroyWireBufferRetains<T>(IntPtr buffer) where T : ISwiftObject
    {
        if (buffer == IntPtr.Zero)
            return;
        TypeMetadata metadata;
        try
        {
            // Resolve via TypeMetadata.GetTypeMetadataOrThrow<T> (reflection-based on Mono),
            // NOT SwiftObjectHelper<T>.GetTypeMetadata(): the latter materializes the generic
            // struct SwiftObjectHelper<T>, whose static-virtual ISwiftObject dispatch crashes
            // Mono JIT (jit-info.c:918) when T is itself a generic instantiation over a method's
            // own type parameter (e.g. SwiftOptional<TValue> from inside a generic wrapper
            // method's cleanup). GetTypeMetadataOrThrow is the Mono-safe reflection path the rest
            // of the runtime already standardized on for exactly this reason.
            metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
        }
        catch
        {
            // Metadata unavailable — generator-emitted ISwiftObject types always
            // resolve metadata in production, but unit-test mock types may not.
            // Skip the destroy; the caller still frees the carrier.
            return;
        }
        DestroyWireBufferRetains(buffer, metadata);
    }

    /// <summary>
    /// Non-generic <see cref="DestroyWireBufferRetains{T}"/> variant that releases a wire buffer's
    /// value-witness retains using already-resolved <paramref name="metadata"/>. Generated copy-out
    /// cleanup uses this form so the generic wrapper method's <c>finally</c> never has to materialize
    /// a fresh generic helper instantiation (e.g.
    /// <c>DestroyWireBufferRetains&lt;SwiftOptional&lt;TValue&gt;&gt;</c>): a brand-new generic
    /// instantiation there shifts Mono JIT native-wrapper generation and can SIGSEGV. The caller
    /// resolves <paramref name="metadata"/> via the cached, Mono-safe
    /// <see cref="TypeMetadata.TryGetTypeMetadata{T}"/> the method body already exercised, so no new
    /// generic type is forced.
    /// </summary>
    /// <param name="buffer">The wire buffer pointer to destroy. <c>IntPtr.Zero</c> is a no-op.</param>
    /// <param name="metadata">The Swift type metadata of the value occupying the buffer.</param>
    public static unsafe void DestroyWireBufferRetains(IntPtr buffer, TypeMetadata metadata)
    {
        if (buffer == IntPtr.Zero)
            return;
        if (!metadata.IsValid)
            return;
        metadata.ValueWitnessTable->Destroy((void*)buffer, metadata);
    }

    /// <summary>
    /// Takes an independent <c>+1</c> copy of a Swift value out of a wire buffer via the
    /// value witness table's <c>InitializeWithCopy</c>: the boxed/inline heap payload is retained
    /// so <paramref name="destination"/> owns a reference whose lifetime is independent of
    /// <paramref name="source"/>. This is the read-side counterpart of
    /// <see cref="DestroyWireBufferRetains(IntPtr, TypeMetadata)"/> — when a Swift accessor returns
    /// an existential heap cell that is freed (deinitialized + deallocated) by a generated free
    /// function, the adopting proxy must hold its <em>own</em> retained copy, or the proxy's later
    /// destroy and the cell free would both release the same payload (double-release → UAF).
    /// <paramref name="destination"/> is treated as raw, uninitialised storage; any prior bytes are
    /// overwritten without running a destructor (the caller passes a fresh or trivially-copied
    /// buffer). No-op when either pointer is null or <paramref name="metadata"/> is invalid.
    /// </summary>
    /// <param name="destination">Uninitialised buffer that receives the retained copy.</param>
    /// <param name="source">The wire buffer holding the Swift value to copy from.</param>
    /// <param name="metadata">The Swift type metadata of the value occupying the buffer.</param>
    public static unsafe void CopyWireBufferRetains(IntPtr destination, IntPtr source, TypeMetadata metadata)
    {
        if (destination == IntPtr.Zero || source == IntPtr.Zero)
            return;
        if (!metadata.IsValid)
            return;
        metadata.ValueWitnessTable->InitializeWithCopy((void*)destination, (void*)source, metadata);
    }

    /// <summary>
    /// The finalizer-thread-safe variant of <see cref="DestroyWireBufferRetains(IntPtr, TypeMetadata)"/>.
    /// Routes the value-witness <c>Destroy</c> through the <c>SBW_VWTDestroy</c> <c>@_cdecl</c>
    /// trampoline (which reads the VWT from <c>metadata[-1]</c> and calls it) instead of invoking
    /// the witness pointer directly. A direct <c>CallConvSwift</c>/VWT call from the GC finalizer
    /// thread crashes Mono with the <c>!ji-&gt;async</c> assertion after CallConvSwift JIT
    /// contamination — the same failure the class-bound proxy release dodges via
    /// <see cref="Arc.UnknownObjectReleaseFinalizerSafe"/>. Owned opaque existential proxies must
    /// use this from their <c>~Proxy()</c> finalizer (audit P0-10). No-op on null buffer / invalid
    /// metadata.
    /// </summary>
    /// <param name="buffer">The wire buffer pointer to destroy. <c>IntPtr.Zero</c> is a no-op.</param>
    /// <param name="metadata">The Swift type metadata of the value occupying the buffer.</param>
    public static void DestroyWireBufferRetainsFinalizerSafe(IntPtr buffer, TypeMetadata metadata)
    {
        if (buffer == IntPtr.Zero)
            return;
        if (!metadata.IsValid)
            return;
        VwtDestroyTrampoline.Destroy(buffer, metadata.Handle);
    }

    /// <summary>
    /// Marshals an <b>owned</b> by-value Swift struct out of a caller-owned temporary into a
    /// managed wrapper, then releases the temporary's value-witness retains. Used for the direct
    /// (by-value register) return of a frozen-with-memory struct: the C# local holds an
    /// initialized Swift value carrying <c>+1</c> retains on its heap fields, <c>NewFromPayload</c>
    /// makes an <c>InitializeWithCopy</c> duplicate owned by the wrapper's <c>SwiftSafeHandle</c>,
    /// and this then destroys the caller's original temporary so its <c>+1</c> is not orphaned on
    /// the stack (C# never runs Swift value destruction when the local goes out of scope). This is
    /// the by-value analogue of <see cref="DestroyWireBufferRetains{T}"/>, which handles the
    /// indirect-result wire-buffer shape.
    /// </summary>
    /// <typeparam name="T">The Swift wrapper type whose value occupies the buffer.</typeparam>
    /// <param name="owned">Pointer to the caller-owned, initialized Swift value to consume.</param>
    public static unsafe T MarshalFromSwiftObjectConsuming<T>(void* owned) where T : ISwiftObject
    {
        try
        {
            return MarshalFromSwiftObject<T>((IntPtr)owned);
        }
        finally
        {
            DestroyWireBufferRetains<T>((IntPtr)owned);
        }
    }

    /// <summary>
    /// Marshals a value out of an initialized Swift value slot the caller <b>owns</b> and will free
    /// <i>raw</i> (no value-witness <c>Destroy</c>) afterward — the Dict/Set eager-snapshot paths
    /// (<c>SwiftDictionary</c>/<c>SwiftSet</c> enumeration + lookup). The returned wrapper must end up
    /// owning the slot's <c>+1</c> exactly once, with the slot's original reference accounted for by
    /// the time the caller frees the buffer. This is the <c>Move</c> sibling of
    /// <see cref="ExtractCopiedValue{T}"/> (the borrowed-source <c>Copy</c> entry); dispatch is
    /// per-ownership-shape because the shapes consume the slot differently and a uniform "copy each
    /// slot + one whole-buffer Destroy" is unsound for the shapes that transfer in place (see below).
    /// <list type="bullet">
    /// <item><b>True Swift class</b> (<see cref="ISwiftObject"/>, not a value type, not
    /// <see cref="ISwiftStruct"/>, metadata <see cref="TypeMetadataKind.Class"/>): the slot
    /// <em>contains</em> the object pointer and <c>NewFromPayload</c> expects that pointer value, so
    /// the slot is dereferenced. The slot's <c>+1</c> transfers to the wrapper — <b>no</b> retain and
    /// <b>no</b> <c>Destroy</c>.</item>
    /// <item><b>ADOPT / COPY reference-backed non-POD</b>, excluding move-on-construction (non-frozen
    /// structs, complex enums, frozen-with-ref structs, <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>,
    /// and the bare-<see cref="ISwiftObject"/> SwiftUI value wrappers): a bitwise read would either
    /// alias the about-to-be-freed slot (ADOPT → use-after-free) or take a fresh <c>+1</c> while
    /// orphaning the slot's original (COPY → leak). Instead <see cref="ExtractCopiedValue{T}"/> builds
    /// an <i>independent</i> wrapper (its own buffer / its own <c>+1</c>), then a value-witness
    /// <c>Destroy</c> consumes the slot's original <c>+1</c>. The <c>Destroy</c> runs <b>only after</b>
    /// the copy succeeds, so a throw leaves the slot intact for the caller's exception-path release —
    /// the slot is therefore consumed atomically (fully, or not at all on throw).</item>
    /// <item><b>Move-on-construction</b> (<see cref="ISwiftMovesPayloadOnConstruction"/>,
    /// i.e. <c>SwiftString</c>), <b>existential containers</b>, and <b>POD</b>/primitives/value-type
    /// structs: a bitwise read transfers the slot's <c>+1</c> (or there is none). An existential
    /// container in particular must NOT be value-witness-copied/destroyed with its static container
    /// metadata at offset 0 (that metadata is not the box's ARC owner — the consumer takes ownership
    /// of the container words instead). <b>No</b> <c>Destroy</c>.</item>
    /// </list>
    /// <para>
    /// Because the ADOPT/COPY branch consumes its own slot in place (not via a deferred whole-buffer
    /// <c>Destroy</c>), callers that pack multiple values into one buffer (<c>CollectEntries</c>'
    /// <c>(K,V)</c> pair) MUST track consumption <b>per slot</b> on the exception path: re-running a
    /// whole-buffer <c>Destroy</c> over an already-consumed slot (a transferred class pointer, or an
    /// already-<c>Destroy</c>ed ADOPT/COPY slot) double-frees it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The element/key/value type occupying the slot.</typeparam>
    /// <param name="slot">Address of the initialized value slot. For classes it holds the object pointer.</param>
    /// <param name="metadata">Runtime metadata for <typeparamref name="T"/>: detects a true class and drives the ADOPT/COPY value-witness <c>Destroy</c>.</param>
    public static unsafe T MarshalMovedValueFromSlot<T>(void* slot, TypeMetadata metadata)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T))
            && !typeof(T).IsValueType
            && !typeof(ISwiftStruct).IsAssignableFrom(typeof(T))
            && metadata.Kind == TypeMetadataKind.Class)
        {
            IntPtr classPointer = *(IntPtr*)slot;
            return MarshalFromSwift<T>(classPointer);
        }

        // ADOPT (non-frozen struct, complex enum, bare-ISwiftObject SwiftUI wrapper) / COPY
        // (frozen-with-ref, SwiftArray/Dictionary/Set) reference-backed non-POD, excluding
        // move-on-construction (SwiftString) which transfers its +1 via the bitwise read below.
        // Copy out an independent wrapper, THEN Destroy the slot's original +1 — Destroy strictly
        // after the copy so a throw leaves the slot intact (the caller's exception path releases it).
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T))
            && !typeof(T).IsValueType
            && !typeof(ISwiftMovesPayloadOnConstruction).IsAssignableFrom(typeof(T))
            && metadata.IsValid
            && metadata.ValueWitnessTable->IsNonPOD)
        {
            T moved = ExtractCopiedValue<T>(slot, metadata.Size);
            metadata.ValueWitnessTable->Destroy(slot, metadata);
            return moved;
        }

        return MarshalFromSwift<T>((IntPtr)slot);
    }

    /// <summary>
    /// Extracts a payload value of type <typeparamref name="T"/> out of a Swift wire carrier — the
    /// <c>Some</c> payload of <c>SwiftOptional&lt;T&gt;</c> or the success/failure payload of
    /// <c>SwiftResult</c> — into a freshly-constructed managed wrapper that owns an <b>independent</b>
    /// reference, leaving the source carrier's reference intact. The caller passes the address of the
    /// source payload bytes (owned by the carrier) and the carrier's value-witness <c>Size</c>; this
    /// allocates the temporary, balances Swift ARC across the three <c>NewFromPayload</c> ownership
    /// shapes, and returns the wrapper.
    /// <para>
    /// <b>Retain.</b> For a non-POD reference-backed payload (COW String/Array/Dictionary/Set storage, a
    /// struct or complex enum embedding class references, or a bare-<see cref="ISwiftObject"/> SwiftUI
    /// value wrapper) a value-witness <c>InitializeWithCopy</c> takes a <c>+1</c> into the temporary so
    /// the extracted wrapper does not share the carrier's only reference — otherwise disposing the
    /// wrapper would over-release storage the carrier still owns and double-free it. The gate is
    /// "reference-backed" (<see cref="ISwiftObject"/> and not a value type) rather than the narrower
    /// <see cref="ISwiftStruct"/>: the buffer-adopting SwiftUI wrappers (<c>Color</c>, <c>AnyView</c>,
    /// <c>Image</c>, <c>Font</c>, <c>Animation</c>, <c>EdgeInsets</c>) are Swift structs projected to a
    /// sealed class <i>without</i> <see cref="ISwiftStruct"/>, yet they adopt the temporary just like an
    /// <see cref="ISwiftStruct"/> and must be retained the same way. A Swift <i>class</i> payload (where
    /// the payload word IS the instance pointer) is retained and marshalled by the caller's class fast
    /// path (metadata <c>Kind == Class</c>) and never reaches here. Value-type <see cref="ISwiftObject"/>
    /// structs are excluded (read by value; their <c>SwiftHandle</c> throws). POD payloads, primitives,
    /// and existential containers carry no ARC references at this layer — and an existential container's
    /// resolved metadata is not value-witness-copyable at offset 0 — so they take a plain bitwise copy.
    /// A non-<see cref="ISwiftObject"/> <i>tuple</i> whose elements embed ARC references (e.g. a tuple
    /// containing a <c>String</c> or class) is read by value here without a whole-tuple value-witness
    /// <c>+1</c>: the bitwise copy aliases the carrier's references at <c>+0</c>, and the subsequent
    /// <c>MarshalFromSwift&lt;T&gt;</c> tuple walk takes the independent per-element <c>+1</c>s via
    /// <see cref="ExtractCopiedElement"/> (so each element wrapper owns its own reference and the
    /// carrier's stays intact). This buffer is then freed raw — correct precisely because it never took
    /// a +1 of its own.
    /// </para>
    /// <para>
    /// <b>Cleanup</b> depends on what <c>NewFromPayload</c> did with the temporary, detected by
    /// comparing the constructed wrapper's <c>SwiftHandle</c> to the temporary's address:
    /// <list type="bullet">
    /// <item><b>ADOPT</b> (non-frozen structs, complex enums, the SwiftUI value wrappers): the wrapper's
    /// SafeHandle wraps the temporary pointer directly and frees+destroys it on dispose.
    /// <c>SwiftHandle == temp</c> — leave it; the wrapper owns the temporary and its <c>+1</c>.</item>
    /// <item><b>COPY</b> (frozen-projected-as-class structs, <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>):
    /// the wrapper allocates its own buffer and <c>InitializeWithCopy</c>s into it, taking a fresh
    /// <c>+1</c>. <c>SwiftHandle != temp</c>, so the temporary's <c>+1</c> is orphaned — value-witness
    /// <c>Destroy</c> it, then free the dead buffer.</item>
    /// <item><b>MOVE</b> (<see cref="ISwiftMovesPayloadOnConstruction"/>, i.e. <c>SwiftString</c>): the
    /// wrapper allocates its own buffer and <i>bitwise</i>-copies the temporary, transferring our
    /// <c>+1</c> into it without taking a new one. <c>SwiftHandle != temp</c>, but destroying the
    /// temporary would over-release the now-shared reference — only free the dead buffer.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <typeparam name="T">The payload value type being extracted.</typeparam>
    /// <param name="source">Address of the source payload bytes (owned by the carrier).</param>
    /// <param name="swiftPayloadSize">The carrier payload's value-witness size in bytes.</param>
    /// <returns>The constructed managed wrapper, owning an independent reference.</returns>
    public static unsafe T MarshalExtractedPayloadValue<T>(void* source, nuint swiftPayloadSize)
    {
        nuint bufferSize = ExtractionBufferSize<T>(swiftPayloadSize);
        byte* heapCopy = (byte*)NativeMemory.AllocZeroed(bufferSize);

        // Reference-backed wrappers store the payload in a SwiftSafeHandle and can adopt or copy the
        // temporary buffer: every ISwiftStruct (non-frozen structs, complex enums, frozen-as-class,
        // String/Array/Dictionary/Set), plus bare ISwiftObject reference types whose NewFromPayload
        // adopts the buffer (the hand-written SwiftUI value wrappers — Color, AnyView, Image, Font,
        // Animation, EdgeInsets — which are Swift structs projected to a sealed class without
        // ISwiftStruct). Value-type ISwiftObject structs (e.g. LargeValueStruct) are excluded: they
        // read by value and their SwiftHandle is the throwing default. True Swift classes never reach
        // here — the callers' class fast path (metadata Kind == Class) handles them first.
        bool referenceBacked = typeof(ISwiftObject).IsAssignableFrom(typeof(T)) && !typeof(T).IsValueType;

        bool retained = false;
        TypeMetadata metadata = default;
        if (referenceBacked
            && TypeMetadata.TryGetTypeMetadata<T>(out var md)
            && md.Value.IsValid
            && md.Value.ValueWitnessTable->IsNonPOD)
        {
            metadata = md.Value;
            metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, source, metadata);
            retained = true;
        }
        else
        {
            new Span<byte>(source, (int)swiftPayloadSize).CopyTo(new Span<byte>(heapCopy, (int)swiftPayloadSize));
        }

        T wrapper;
        try
        {
            wrapper = MarshalFromSwift<T>((IntPtr)heapCopy);
        }
        catch
        {
            // NewFromPayload threw before adopting the temporary: release our +1 (if any) and free it.
            if (retained)
                metadata.ValueWitnessTable->Destroy(heapCopy, metadata);
            NativeMemory.Free(heapCopy);
            throw;
        }

        // Only reference-backed wrappers (SafeHandle-storing — every ISwiftStruct and the bare
        // ISwiftObject SwiftUI wrappers) can adopt the temporary buffer. Their SwiftHandle is
        // meaningful, so the adopt/copy/move shape is detected by comparing it to the temporary's
        // address. (wrapper is ISwiftObject is a null guard; referenceBacked already implies the type.)
        if (referenceBacked && wrapper is ISwiftObject swiftObj)
        {
            if (swiftObj.SwiftHandle != (IntPtr)heapCopy)
            {
                // The wrapper made its own buffer (COPY or MOVE shape); the temporary is now dead.
                // COPY shapes took their own +1, leaving ours orphaned — destroy it. MOVE shapes
                // (ISwiftMovesPayloadOnConstruction) transferred our +1 into the wrapper, so destroying
                // would over-release; only free the buffer.
                if (retained && wrapper is not ISwiftMovesPayloadOnConstruction)
                    metadata.ValueWitnessTable->Destroy(heapCopy, metadata);
                NativeMemory.Free(heapCopy);
            }
            // else ADOPT: the wrapper's SafeHandle owns the temporary (and its +1); leave it.
        }
        else
        {
            // Read-by-value wrappers that never adopt the buffer: non-ISwiftObject values (existential
            // containers, primitives, tuples) via Unsafe.Read, and frozen blittable ISwiftObject value
            // structs (e.g. LargeValueStruct, whose NewFromPayload returns *(T*)handle by value — note
            // its SwiftHandle is the throwing default, so it must NOT be compared above). Free the
            // temporary; nothing references it. (retained is always false here — the retain gate is
            // referenceBacked — so there is no +1 to release.)
            NativeMemory.Free(heapCopy);
        }

        return wrapper;
    }

    /// <summary>
    /// Extracts a value of type <typeparamref name="T"/> out of a <b>borrowed</b> Swift source — an
    /// Optional/Result wire-carrier payload, or a stream element pointer whose backing slot is only
    /// valid for the duration of a Swift callback — into a freshly-constructed managed wrapper that owns
    /// an <b>independent</b> reference, leaving the source's reference intact. This is the <c>Copy</c>
    /// sibling of <see cref="MarshalMovedValueFromSlot{T}"/> (<c>Move</c>): the unified entry point for
    /// "the source keeps its <c>+1</c>, the result must own a separate <c>+1</c>" extraction.
    /// <para>
    /// Dispatch is per-ownership-shape:
    /// <list type="bullet">
    /// <item><b>True Swift class</b> (<see cref="ISwiftObject"/>, not a value type, not
    /// <see cref="ISwiftStruct"/>, metadata <c>Kind == Class</c>): the payload word at offset 0 <i>is</i>
    /// the instance pointer. Dereference it, take an independent ObjC-aware
    /// <see cref="Arc.UnknownObjectRetain(System.IntPtr)"/> (<c>swift_unknownObjectRetain</c> dispatches by
    /// isa, so it is correct for both pure-Swift and <c>@objc : NSObject</c>-rooted classes — native-only
    /// <c>swift_retain</c> no-ops/over-releases on an NSObject subclass; audit P1-01), and marshal the
    /// pointer directly — <c>NewFromPayload</c> for a class expects the pointer value, not the address
    /// holding it. This folds in the hand-rolled class fast path that <c>SwiftOptional.Some</c> and
    /// <c>SwiftResult.ExtractPayloadValue</c> previously each carried.</item>
    /// <item><b>Everything else</b> (value types, <see cref="ISwiftStruct"/> wrappers, bare-<see
    /// cref="ISwiftObject"/> struct wrappers, primitives, existential containers): delegate to
    /// <see cref="MarshalExtractedPayloadValue{T}"/>, which takes a value-witness <c>+1</c> for non-POD
    /// reference-backed payloads and balances ARC across the adopt/copy/move <c>NewFromPayload</c> shapes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The borrowed-source contract is why stream elements (<c>SwiftAsyncStream.OnElement</c>) MUST route
    /// through here rather than <see cref="MarshalFromSwift{T}"/>: the Swift producer passes
    /// <c>withUnsafePointer(to: element)</c>, a pointer valid only during the callback, and still owns
    /// (and will release) its own reference. A bare <c>MarshalFromSwift</c> would either alias the
    /// soon-to-die slot (class/adopt shapes → use-after-free) or bitwise-move a borrowed <c>+0</c> as if
    /// it were a <c>+1</c> (move-on-construction shapes like <c>SwiftString</c> → double-release). Copying
    /// out an independent reference during the callback closes both.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The value type being extracted.</typeparam>
    /// <param name="source">Address of the borrowed source bytes (still owned by the caller/Swift).</param>
    /// <param name="swiftPayloadSize">The source payload's value-witness size in bytes.</param>
    /// <returns>The constructed managed wrapper, owning an independent reference.</returns>
    public static unsafe T ExtractCopiedValue<T>(void* source, nuint swiftPayloadSize)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T))
            && !typeof(T).IsValueType
            && !typeof(ISwiftStruct).IsAssignableFrom(typeof(T))
            && TypeMetadata.TryGetTypeMetadata<T>(out var classMd)
            && classMd.Value.IsValid
            && classMd.Value.Kind == TypeMetadataKind.Class)
        {
            IntPtr classPointer = *(IntPtr*)source;
            Arc.UnknownObjectRetain(classPointer);
            return MarshalFromSwift<T>(classPointer);
        }

        return MarshalExtractedPayloadValue<T>(source, swiftPayloadSize);
    }

    /// <summary>
    /// Copies a <b>true Swift class</b> (pure-Swift or <c>@objc : NSObject</c>) out of a
    /// <b>borrowed</b> slot — <paramref name="slot"/> is the address of a word holding the Swift
    /// heap-object pointer — into a managed wrapper that owns an <b>independent</b> reference,
    /// leaving the slot's own reference intact.
    /// <para>
    /// This is the copy-out a protocol-proxy reverse-callback receiver must use for a Swift-class
    /// parameter (justinwojo/swift-dotnet-bindings#40): the generated Swift thunk passes
    /// <c>&amp;{param}Copy</c>, so the receiver is handed the <i>address</i> of a slot, not the
    /// instance pointer. The per-proxy local <c>MarshalFromSwift&lt;T&gt;</c> does
    /// <c>Unsafe.Read&lt;T&gt;</c>, which reinterprets the Swift heap pointer as a managed reference
    /// and SIGSEGVs on first use. Dereference the slot, take an independent ObjC-aware
    /// <see cref="Arc.UnknownObjectRetain"/> (<c>swift_unknownObjectRetain</c> dispatches by isa, so
    /// it is correct for both pure-Swift and NSObject-rooted classes — native-only
    /// <c>swift_retain</c> is a no-op/over-release on an NSObject subclass; audit P1-01), and build
    /// the wrapper from the pointer via <see cref="MarshalFromSwift{T}"/> (<c>NewFromPayload</c> for
    /// a class wants the pointer value, not the address holding it).
    /// </para>
    /// </summary>
    /// <typeparam name="T">The Swift-class wrapper type.</typeparam>
    /// <param name="slot">Address of the borrowed slot holding the Swift class pointer.</param>
    /// <returns>The constructed wrapper, owning an independent reference.</returns>
    public static unsafe T MarshalBorrowedClassFromSlot<T>(IntPtr slot)
    {
        IntPtr classPointer = *(IntPtr*)slot;
        Arc.UnknownObjectRetain(classPointer);
        return MarshalFromSwift<T>(classPointer);
    }

    /// <summary>
    /// The <b>owned</b> sibling of <see cref="MarshalBorrowedClassFromSlot{T}"/>: <paramref name="slot"/>
    /// is the address of a carrier word holding a Swift class pointer whose <c>+1</c> the slot
    /// <b>owns</b> and transfers to the returned wrapper. Dereference the slot to get the instance
    /// pointer, then build the wrapper via <see cref="MarshalFromSwift{T}"/> (<c>NewFromPayload</c>
    /// for a class adopts the pointer's existing <c>+1</c> with <b>no extra retain</b>) — the move
    /// semantics of <see cref="MarshalMovedValueFromSlot{T}"/>'s class branch, not the borrowed
    /// copy-out's independent retain.
    /// <para>
    /// This is the receiver for a generic-parameter indirect return specialized to a class conformer
    /// (audit P0-11): the Swift wrapper does <c>resultPtr.initializeMemory(as: (C).self, repeating:
    /// _result, count: 1)</c>, which stores the instance pointer <i>into</i> the carrier and leaves
    /// the carrier owning a single <c>+1</c>. Wrapping the carrier <i>address</i> directly (the prior
    /// <c>MarshalFromSwift&lt;C&gt;(resultPtr)</c>) reinterprets the carrier as the instance and
    /// use-after-frees it once the caller raw-frees the carrier, while leaking the real instance's
    /// <c>+1</c>. The caller still raw-frees the carrier bytes after this call: the <c>+1</c> moved to
    /// the wrapper, so freeing the carrier word releases no reference.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The Swift-class wrapper type.</typeparam>
    /// <param name="slot">Address of the owned carrier slot holding the Swift class pointer.</param>
    /// <returns>The constructed wrapper, adopting the carrier's transferred reference.</returns>
    public static unsafe T MarshalOwnedClassFromSlot<T>(IntPtr slot)
    {
        IntPtr classPointer = *(IntPtr*)slot;
        return MarshalFromSwift<T>(classPointer);
    }

    /// <summary>
    /// The <c>Optional&lt;class&gt;</c> sibling of <see cref="MarshalBorrowedClassFromSlot{T}"/>. A
    /// Swift <c>Optional</c> of a class is nil-pointer-optimised: the borrowed slot holds either a
    /// null word (<c>nil</c> → <c>null</c>) or the class pointer (non-nil → copy out with an
    /// ObjC-aware retain). Reading it as a managed <c>SwiftOptional&lt;T&gt;</c> via
    /// <c>Unsafe.Read</c> reinterprets that single word as a managed object and crashes the same way
    /// the non-optional path does.
    /// </summary>
    /// <typeparam name="T">The Swift-class wrapper type.</typeparam>
    /// <param name="slot">Address of the borrowed slot holding the nil-pointer-optimised payload.</param>
    /// <returns>The constructed wrapper, or <c>null</c> when the optional is <c>nil</c>.</returns>
    public static unsafe T? MarshalBorrowedOptionalClassFromSlot<T>(IntPtr slot) where T : class
    {
        IntPtr classPointer = *(IntPtr*)slot;
        if (classPointer == IntPtr.Zero)
            return null;
        Arc.UnknownObjectRetain(classPointer);
        return MarshalFromSwift<T>(classPointer);
    }

    /// <summary>
    /// The by-pointer sibling of <see cref="MarshalBorrowedClassFromSlot{T}"/>, for closure
    /// callback parameters where the cdecl thunk passes the Swift class pointer <em>directly</em>
    /// (not the address of a slot holding it). Takes an independent ARC <c>+1</c> on the borrowed
    /// pointer and builds an <b>owning</b> wrapper, so the wrapper's <c>SwiftSafeHandle</c> balances
    /// that retain on both <c>Dispose</c> and finalize. This replaces the older
    /// <see cref="MarshalBorrowedFromSwift{T}"/> path for class parameters, whose
    /// <c>GC.SuppressFinalize</c>-only strategy left an explicit <c>Dispose</c> in the user's
    /// callback body double-releasing a <c>+0</c> borrowed handle. The retain routes through the
    /// kind-dispatching <see cref="Arc.UnknownObjectRetain"/> so an Objective-C-backed class is
    /// retained correctly (the same isa-aware entry point the receiver path uses).
    /// </summary>
    /// <typeparam name="T">The Swift-class wrapper type.</typeparam>
    /// <param name="classPointer">The borrowed Swift class pointer passed by the cdecl callback.</param>
    /// <returns>The constructed wrapper, owning an independent reference.</returns>
    public static unsafe T MarshalBorrowedClassFromSwift<T>(IntPtr classPointer)
    {
        Arc.UnknownObjectRetain(classPointer);
        return MarshalFromSwift<T>(classPointer);
    }

    /// <summary>
    /// Computes the size of the destination buffer an extracted-by-copy payload of type
    /// <typeparamref name="T"/> must be allocated with, given the Swift payload's own size
    /// (<paramref name="swiftPayloadSize"/>, the enum/container value-witness <c>Size</c>).
    /// <para>
    /// For value-type payloads, <see cref="MarshalFromSwift{T}"/> reads <c>Unsafe.SizeOf&lt;T&gt;()</c>
    /// bytes via <c>Unsafe.Read&lt;T&gt;</c>. The managed projection can be <b>larger</b> than the Swift
    /// payload it is extracted from — most notably <c>any Error</c>, which Swift represents as a
    /// compact single-word boxed existential (8 bytes) but C# models as the general 5-word
    /// <c>ExistentialContainer1</c> (40 bytes). A buffer sized only to the Swift payload would be read
    /// off the end, fabricating the unused container slots from adjacent-heap garbage. Returning the
    /// larger of the two sizes (paired with a zeroed allocation and copying only the Swift bytes) keeps
    /// the fixed-size read in bounds and leaves the unused slots zero.
    /// </para>
    /// <para>
    /// <see cref="ISwiftObject"/> payloads (including <see cref="ISwiftStruct"/>) marshal via
    /// <c>NewFromPayload</c>, which is metadata-driven rather than a blind <c>sizeof(T)</c> read, so the
    /// Swift payload size is already correct for them and is returned unchanged.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The payload value type being extracted.</typeparam>
    /// <param name="swiftPayloadSize">The Swift payload's value-witness size in bytes.</param>
    /// <returns>The number of bytes to allocate for the extraction buffer.</returns>
    public static nuint ExtractionBufferSize<T>(nuint swiftPayloadSize)
    {
        if (!typeof(ISwiftObject).IsAssignableFrom(typeof(T)))
        {
            nuint managedSize = (nuint)Unsafe.SizeOf<T>();
            if (managedSize > swiftPayloadSize)
                return managedSize;
        }
        return swiftPayloadSize;
    }

    /// <summary>
    /// Pre-registers a NewFromPayload factory for a type so NativeAOT can create instances
    /// without reflection. Called by generated [ModuleInitializer] code at assembly load time.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type to register.</typeparam>
    public static void RegisterSwiftObjectFactory<T>() where T : ISwiftObject
    {
        NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
    }

    /// <summary>
    /// Pre-registers a protocol conformance factory for a (type, protocol) pair so NativeAOT
    /// can resolve conformances without reflection. Called by generated [ModuleInitializer] code.
    /// </summary>
    /// <typeparam name="TType">The ISwiftObject type.</typeparam>
    /// <typeparam name="TProtocol">The protocol interface type.</typeparam>
    public static void RegisterConformanceFactory<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        ConformanceDispatcher.Register(typeof(TType), typeof(TProtocol),
            () => TType.GetProtocolConformanceDescriptor<TProtocol>());
    }

    /// <summary>
    /// Pre-registers a protocol witness table for a (type, protocol) pair so
    /// SwiftDictionary/SwiftSet can resolve witness tables without reflection on NativeAOT.
    /// Called by generated [ModuleInitializer] code at assembly load time.
    /// The witness table is computed eagerly at registration time using the direct dispatch path.
    /// </summary>
    /// <typeparam name="TType">The ISwiftObject type (e.g., a struct conforming to Hashable).</typeparam>
    /// <typeparam name="TProtocol">The protocol interface type (e.g., ISwiftHashable).</typeparam>
    public static void RegisterWitnessTable<TType, TProtocol>()
        where TType : ISwiftObject
        where TProtocol : class
    {
        // GetOrThrowDirect uses static virtual dispatch (NativeAOT-only).
        // On Mono (JIT or AOT/simulator), witness tables are resolved via reflection
        // at call time — no pre-registration needed.
        if (!SwiftRuntimeInfo.IsNativeAotRuntime)
            return;
        var witnessTable = ProtocolWitnessTable.GetOrThrowDirect<TType, TProtocol>();
        WitnessTableDispatcher.Register(typeof(TType), typeof(TProtocol), witnessTable);
    }

    /// <summary>
    /// Marshals a value to a Swift destination
    /// </summary>
    /// <typeparam name="T">The type of the value being marshaled</typeparam>
    /// <param name="value">The value to marshal</param>
    /// <param name="swiftDestSpan">the destination for marshaling</param>
    /// <returns>the number of bytes written to the destination</returns>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    public static int MarshalToSwift<T>(T value, ref Span<byte> swiftDestSpan)
    {
        if (value is ISwiftObject swiftValue)
        {
            return swiftValue.MarshalToSwift(ref swiftDestSpan);
        }

        var type = typeof(T);
        if ((type.IsPrimitive || typeof(nint).IsAssignableFrom(type) || typeof(nuint).IsAssignableFrom(type)) && !typeof(char).IsAssignableFrom(type))
        {
            unsafe
            {
                int size = Unsafe.SizeOf<T>();
                if (size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    MarshalPrimitiveToSwift(value, swiftDest);
                    return size;
                }
            }
        }

        // Handle tuple types (ValueTuple<T1, T2, ...>)
        // Note: Tuple marshalling uses reflection internally, but this is intentional
        // for the generic runtime path. Generated bindings use inline code instead.
        if (TypeMetadata.IsValueTupleType(type))
        {
            return MarshalTupleToSwift(value, type, ref swiftDestSpan);
        }

        // Handle delegate types (closures)
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            unsafe
            {
                if (value is Delegate delegateValue)
                {
                    // For now, only support @convention(c) closures which are just function pointers
                    // Escaping closures require a thunk which is generated by the emitter
                    var closureData = SwiftClosureMarshaller.CreateConventionCClosure(delegateValue);

                    // Write the closure data (function pointer + context) to the destination
                    int closureSize = sizeof(SwiftClosureData);
                    if (closureSize > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match closure size, Expected: {closureSize}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        *(SwiftClosureData*)swiftDest = closureData;
                        return closureSize;
                    }
                }
            }
        }

        // Handle existential containers (Swift protocol types like 'any Protocol')
        if (typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            if (value is IExistentialContainer container)
            {
                int containerSize = container.SizeOf;
                if (containerSize > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match container size, Expected: {containerSize}, Actual: {swiftDestSpan.Length}");
                }
                unsafe
                {
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        container.CopyTo((IntPtr)swiftDest);
                        return containerSize;
                    }
                }
            }
        }

        // ClassExistentialContainer1 (the compact 2-word [classRef][witnessTable] SwiftArray
        // element carrier for class-bound `any P`) is a pure-blittable value type and falls into
        // the branch below as a raw 16-byte write — deliberately +0. Its array-element ownership is
        // NOT established here: every SwiftArray write entry point that takes the carrier is
        // __owned (consumes at +1) and the array's class-existential value-witness table releases
        // word0 on destroy, so the +1 the array consumes is minted/donated UPSTREAM by
        // ExistentialContainerFactory.CreateOwnedClassCarrier (which the emitter calls for every
        // class-bound `[any P]` param/write-direction element). Retaining word0 here too would
        // double-count the boxable conformer's existing +1 and leak it. See that helper and
        // ClassExistentialContainer1.FromExistentialContainer1 for the ownership contract.

        // Handle blittable value types: C# enums (simple enums) and frozen structs
        // (CGPoint, CGRect, CGSize, etc.). These have no managed references and can be
        // written directly as raw bytes. Primitives are already handled above.
        if (type.IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            unsafe
            {
                int size = Unsafe.SizeOf<T>();
                // Simple enum size mismatch: C# enum : int is 4 bytes, but Swift simple enums
                // use the minimum bytes needed for the discriminator (1 byte for ≤256 cases,
                // 2 for ≤65536, 4 for larger). When the caller provides a Swift-sized span
                // (e.g., SwiftArray ElementSize), narrow the C# int to the Swift width
                // instead of throwing or overwriting adjacent memory.
                if (size > swiftDestSpan.Length && type.IsEnum &&
                    !typeof(ISwiftObject).IsAssignableFrom(type) && swiftDestSpan.Length >= 1)
                {
                    int enumValue = Convert.ToInt32(value);
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        switch (swiftDestSpan.Length)
                        {
                            case 1:
                                ((byte*)swiftDest)[0] = (byte)enumValue;
                                break;
                            case 2:
                                *(short*)swiftDest = (short)enumValue;
                                break;
                            default:
                                *(int*)swiftDest = enumValue;
                                break;
                        }
                        return swiftDestSpan.Length;
                    }
                }
                if (size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    Unsafe.Write(swiftDest, value);
                    return size;
                }
            }
        }

        throw new NotSupportedException($"Cannot marshal type {type} to Swift");
    }

    /// <summary>
    /// Marshals a primitive value to a Swift destination
    /// </summary>
    /// <typeparam name="T">the type of the primitive</typeparam>
    /// <param name="value">The value to marshal</param>
    /// <param name="swiftDest">where in memory to marshal it</param>
    /// <returns>the resulting pointer for passing to a Swift method.</returns>
    /// <exception cref="NotSupportedException"></exception>
    static unsafe void MarshalPrimitiveToSwift<T>(T value, void* swiftDest)
    {
        if (value is bool boolValue)
        {
            *((byte*)swiftDest) = (byte)(boolValue ? 1 : 0);
        }
        else if (value is byte byteValue)
        {
            *((byte*)swiftDest) = byteValue;
        }
        else if (value is sbyte sbyteValue)
        {
            *((sbyte*)swiftDest) = sbyteValue;
        }
        else if (value is short shortValue)
        {
            *((short*)swiftDest) = shortValue;
        }
        else if (value is ushort ushortValue)
        {
            *((ushort*)swiftDest) = ushortValue;
        }
        else if (value is int intValue)
        {
            *((int*)swiftDest) = intValue;
        }
        else if (value is uint uintValue)
        {
            *((uint*)swiftDest) = uintValue;
        }
        else if (value is long longValue)
        {
            *((long*)swiftDest) = longValue;
        }
        else if (value is ulong ulongValue)
        {
            *((ulong*)swiftDest) = ulongValue;
        }
        else if (value is float floatValue)
        {
            *((float*)swiftDest) = floatValue;
        }
        else if (value is double doubleValue)
        {
            *((double*)swiftDest) = doubleValue;
        }
        else if (value is nint nintValue)
        {
            *((nint*)swiftDest) = nintValue;
        }
        else if (value is nuint nuintValue)
        {
            *((nuint*)swiftDest) = nuintValue;
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal type {typeof(T)} to Swift");
        }
    }

    /// <summary>
    /// Marshals an ISwiftObject value from a Swift source.
    /// NativeAOT-safe: uses direct static virtual dispatch instead of reflection.
    /// Generated bindings should prefer this overload when T is known to implement ISwiftObject.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The C# object created by marshaling</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    public static T MarshalFromSwiftObject<T>(IntPtr swiftSource) where T : ISwiftObject
    {
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            // Register factory so unconstrained callers (MarshalFromSwift<T>) can use it later.
            NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
            return (T)DirectNewFromPayload<T>(swiftSource);
        }
        return (T)SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(T), swiftSource);
    }

    /// <summary>
    /// Direct static virtual dispatch for NewFromPayload — NativeAOT only.
    /// Separate method so Mono JIT never compiles this.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISwiftObject DirectNewFromPayload<T>(IntPtr swiftSource) where T : ISwiftObject
    {
        return T.NewFromPayload(swiftSource);
    }

    /// <summary>
    /// Marshals a value from a Swift source.
    /// </summary>
    /// <typeparam name="T">The type of the expected value</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The C# type created by marshaling</returns>
    /// <exception cref="NotSupportedException"></exception>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "RunClassConstructor is a NativeAOT fallback in try-catch; type is always an ISwiftObject whose static constructor is preserved")]
    public static T MarshalFromSwift<T>(IntPtr swiftSource)
    {
        return MarshalFromSwiftCore<T>(swiftSource);
    }

    /// <summary>
    /// Marshals a borrowed Swift reference into a non-owning C# wrapper.
    /// Used for closure callback parameters where the native handle is borrowed from Swift
    /// (the caller owns the reference). The wrapper's finalizer is suppressed to prevent
    /// double-release when the GC collects it.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Delegates to MarshalFromSwiftCore")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Delegates to MarshalFromSwiftCore")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Delegates to MarshalFromSwiftCore")]
    [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "Delegates to MarshalFromSwiftCore")]
    [UnconditionalSuppressMessage("Trimming", "IL2059", Justification = "Delegates to MarshalFromSwiftCore")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProperty on ISwiftObject types whose Payload property is always preserved")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GetType().GetProperty on ISwiftObject types whose Payload property is always preserved via TrimmerRoots.xml")]
    public static T MarshalBorrowedFromSwift<T>(IntPtr swiftSource)
    {
        var obj = MarshalFromSwiftCore<T>(swiftSource);
        if (obj != null)
        {
            GC.SuppressFinalize(obj);
            // Generated wrapper classes hold a SafeHandle in a Payload property.
            // The SafeHandle's finalizer calls ReleaseHandle (Arc.Release / VWT.Destroy),
            // which would double-release a borrowed native handle.
            if (obj is ISwiftObject)
            {
                var payloadProp = obj.GetType().GetProperty("Payload",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (payloadProp?.GetValue(obj) is object payload)
                    GC.SuppressFinalize(payload);
            }
        }
        return obj;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved via TrimmerRoots.xml")]
    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "RunClassConstructor is a NativeAOT fallback in try-catch; type is always an ISwiftObject whose static constructor is preserved")]
    private static T MarshalFromSwiftCore<T>(IntPtr swiftSource)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T)))
        {
            // Try factory cache first (populated by constrained code paths on NativeAOT).
            // This avoids reflection entirely for types that have been accessed through
            // SwiftObjectHelper<T> or MarshalFromSwiftObject<T>.
            var cached = NewFromPayloadDispatcher.TryCreate(typeof(T), swiftSource);
            if (cached != null)
                return (T)cached;

            // NativeAOT fallback: trigger type initialization to populate factory cache.
            // Reflection on explicit interface implementations of generic types may fail
            // on NativeAOT. RunClassConstructor triggers static init which calls
            // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata(),
            // registering the NewFromPayload factory.
            if (SwiftRuntimeInfo.IsNativeAotRuntime)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    cached = NewFromPayloadDispatcher.TryCreate(typeof(T), swiftSource);
                    if (cached != null)
                        return (T)cached;
                }
                catch { }
            }

            // Fallback: reflection. Works on Mono JIT always; works on NativeAOT only
            // for types preserved via TrimmerRoots.xml (Swift.Runtime types).
            return (T)SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(T), swiftSource);
        }
        var type = typeof(T);
        if (type.IsPrimitive)
        {
            unsafe
            {
                return MarshalPrimitiveFromSwift<T>(swiftSource);
            }
        }

        // Handle tuple types (ValueTuple<T1, T2, ...>)
        // Note: Tuple marshalling uses reflection internally, but this is intentional
        // for the generic runtime path. Generated bindings use inline code instead.
        if (TypeMetadata.IsValueTupleType(type))
        {
            return MarshalTupleFromSwift<T>(swiftSource);
        }

        // Handle existential container types (blittable structs with fixed layout)
        if (typeof(IExistentialContainer).IsAssignableFrom(type))
        {
            unsafe { return Unsafe.Read<T>((void*)swiftSource); }
        }

        // Handle delegate types (closures) - Phase 3 support
        if (typeof(Delegate).IsAssignableFrom(typeof(T)))
        {
            // Read the Swift closure data (function pointer + context)
            unsafe
            {
                var closureData = *(SwiftClosureData*)swiftSource;

                // Receiving Swift closures as C# delegates requires generated invoker code
                // because we need to know the exact signature to call the Swift function
                // with the proper calling convention (context in register).
                // The generated bindings should create SwiftEscapingClosure<TDelegate> wrappers
                // with proper invoker delegates.
                throw new NotSupportedException(
                    $"Receiving Swift closures as C# delegates requires generated invoker code. " +
                    $"The closure data is at address 0x{swiftSource:X}, " +
                    $"function pointer: 0x{closureData.FunctionPointer:X}, " +
                    $"context: 0x{closureData.Context:X}. Type: {typeof(T)}");
            }
        }

        // Existential containers cannot be directly marshalled from Swift to C# as a generic delegate
        // because the concrete type is not known at compile time. The generated bindings should
        // handle existential types with explicit container types.

#if IOS || TVOS || MACCATALYST || MACOS
        // Defense-in-depth: if T is an NSObject subclass (ObjC-bridged type like UIImage, NSImage),
        // read the object pointer from the Swift memory and wrap with GetNSObject<T>.
        // The generated bindings should emit GetNSObject directly, but this catches edge cases
        // where the TypeDatabase didn't recognize the type as ObjC-bridged.
        if (typeof(Foundation.NSObject).IsAssignableFrom(type))
        {
            var objPtr = Marshal.ReadIntPtr(swiftSource);
            return (T)(object)ObjCRuntime.Runtime.GetNSObject(objPtr)!;
        }
#endif

        // Simple enum fast path: Swift simple enums use the minimum bytes for the
        // discriminator (1 for ≤256 cases, 2 for ≤65536, etc.), but C# enum : int is
        // always 4 bytes. Read only the Swift-sized bytes to avoid overreading.
        // With metadata: use exact Size. Without metadata: default to 1 byte (covers
        // enums with ≤256 cases; enums with 256+ cases require metadata registration).
        if (type.IsEnum && !typeof(ISwiftObject).IsAssignableFrom(type) &&
            !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            int csharpSize = Unsafe.SizeOf<T>();
            int swiftSize = TypeMetadata.TryGetTypeMetadata<T>(out var enumMeta)
                ? (int)enumMeta.Value.Size
                : 1; // Default to 1 byte for unregistered simple enums (≤256 cases)
            if (swiftSize < csharpSize)
            {
                unsafe
                {
                    int caseValue = swiftSize switch
                    {
                        1 => ((byte*)swiftSource)[0],
                        2 => *(short*)swiftSource,
                        _ => *(int*)swiftSource,
                    };
                    return (T)Enum.ToObject(typeof(T), caseValue);
                }
            }
            // If Swift size >= C# size, fall through to the blittable path below
        }

        // Blittable value types: frozen structs (CGPoint, CGRect, CGSize).
        // Read directly from native memory. Complex enums implement ISwiftObject
        // and are handled above. Simple enums are handled above.
        // Gate: must be unmanaged (no managed references) to avoid invalid managed pointers.
        if (type.IsValueType && RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false)
        {
            unsafe { return Unsafe.Read<T>((void*)swiftSource); }
        }

        throw new NotSupportedException($"Cannot marshal type {type} from Swift");
    }

    /// <summary>
    /// Reads a Swift Optional value from a raw memory pointer and returns as C# nullable.
    /// The pointer must point to a Swift Optional&lt;T&gt; layout (value bytes + tag byte).
    /// Used by generated closure callbacks that receive heap-allocated Optional values.
    /// Uses direct memory reads to avoid SwiftOptional metadata resolution, which crashes
    /// in Mono JIT UnmanagedCallersOnly context.
    /// </summary>
    /// <typeparam name="T">The value type (primitive or enum) wrapped in Optional.</typeparam>
    /// <param name="ptr">Pointer to the heap-allocated Swift Optional&lt;T&gt; memory.</param>
    /// <returns>The value as C# nullable, or null if the Optional is .none.</returns>
    public static unsafe T? MarshalOptionalFromSwift<T>(IntPtr ptr) where T : struct
    {
        // Swift Optional<T> layout depends on the type:
        // - Primitives (Int32, Int64, Double, etc.): [value bytes] [1 byte tag], tag 0=Some, 1=None
        // - Bool: extra inhabitant encoding — 1 byte total, value > 1 means None
        // - Simple enums: may use extra inhabitants depending on the number of cases

        if (typeof(T) == typeof(bool))
        {
            // Optional<Bool> uses extra inhabitant: 0=false, 1=true, 2=None
            byte rawByte = *(byte*)ptr;
            if (rawByte > 1)
                return null;
            return (T)(object)(rawByte == 1);
        }

        // For primitives (Int32, Double, etc.): tag byte is appended after the value
        int tagOffset = GetPrimitiveTagOffset<T>();
        if (tagOffset > 0)
        {
            byte tag = *((byte*)ptr + tagOffset);
            if (tag != 0)
                return null;
            return Unsafe.ReadUnaligned<T>(ref *(byte*)ptr);
        }

        // For enums and other types: use SwiftOptional metadata path
        // This may not work in all Mono JIT contexts — skip those tests if needed
        using var opt = MarshalFromSwift<SwiftOptional<T>>(ptr);
        return opt.Case == SwiftOptionalCases.Some ? opt.Some : null;
    }

    /// <summary>
    /// Returns the tag byte offset for known blittable primitive types in Swift Optional layout.
    /// Returns -1 for unknown types (enums, structs, etc.).
    /// </summary>
    private static int GetPrimitiveTagOffset<T>() where T : struct
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
            return 1;
        if (typeof(T) == typeof(short) || typeof(T) == typeof(ushort))
            return 2;
        if (typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(float))
            return 4;
        if (typeof(T) == typeof(long) || typeof(T) == typeof(ulong) || typeof(T) == typeof(double))
            return 8;
        if (typeof(T) == typeof(nint) || typeof(T) == typeof(nuint))
            return IntPtr.Size;
        return -1;
    }

    /// <summary>
    /// Marshals a primitive value from a Swift source
    /// </summary>
    /// <typeparam name="T">The type of the value to marshal</typeparam>
    /// <param name="swiftSource">Memory to read from</param>
    /// <returns>The marshaled type</returns>
    /// <exception cref="NotSupportedException"></exception>
    public static unsafe T MarshalPrimitiveFromSwift<T>(IntPtr swiftSource)
    {
        if (typeof(T) == typeof(bool))
        {
            return (T)(object)(((*(byte*)swiftSource) & 1) != 0);
        }
        else if (typeof(T) == typeof(byte))
        {
            return (T)(object)(*(byte*)swiftSource);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            return (T)(object)(*(sbyte*)swiftSource);
        }
        else if (typeof(T) == typeof(short))
        {
            return (T)(object)(*(short*)swiftSource);
        }
        else if (typeof(T) == typeof(ushort))
        {
            return (T)(object)(*(ushort*)swiftSource);
        }
        else if (typeof(T) == typeof(int))
        {
            return (T)(object)(*(int*)swiftSource);
        }
        else if (typeof(T) == typeof(uint))
        {
            return (T)(object)(*(uint*)swiftSource);
        }
        else if (typeof(T) == typeof(long))
        {
            return (T)(object)(*(long*)swiftSource);
        }
        else if (typeof(T) == typeof(ulong))
        {
            return (T)(object)(*(ulong*)swiftSource);
        }
        else if (typeof(T) == typeof(float))
        {
            return (T)(object)(*(float*)swiftSource);
        }
        else if (typeof(T) == typeof(double))
        {
            return (T)(object)(*(double*)swiftSource);
        }
        else if (typeof(T) == typeof(nint))
        {
            return (T)(object)(*(nint*)swiftSource);
        }
        else if (typeof(T) == typeof(nuint))
        {
            return (T)(object)(*(nuint*)swiftSource);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal type {typeof(T)} from Swift");
        }
    }

    /// <summary>
    /// Marshals a C# ValueTuple to Swift memory.
    /// Uses direct unsafe memory access for primitive types to avoid reflection overhead.
    /// </summary>
    /// <typeparam name="T">The ValueTuple type.</typeparam>
    /// <param name="value">The tuple value.</param>
    /// <param name="tupleType">The tuple type.</param>
    /// <param name="swiftDestSpan">The destination span.</param>
    /// <returns>The number of bytes written.</returns>
    [RequiresDynamicCode("Tuple marshalling uses reflection for non-primitive element types")]
    [RequiresUnreferencedCode("Tuple marshalling requires access to ValueTuple fields")]
    private static unsafe int MarshalTupleToSwift<T>(T value, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type tupleType, ref Span<byte> swiftDestSpan)
    {
        var elementTypes = TypeMetadata.GetTupleElementTypes(tupleType);
        var elementCount = elementTypes.Length;

        // Get tuple metadata to determine layout
        if (!TypeMetadata.TryGetTypeMetadata<T>(out var tupleMetadata))
            throw new NotSupportedException($"Cannot get tuple metadata for {tupleType.Name}");

        var tupleSize = (int)tupleMetadata.Value.Size;
        if (tupleSize > swiftDestSpan.Length)
            throw new ArgumentException($"Span size does not match tuple size, Expected: {tupleSize}, Actual: {swiftDestSpan.Length}");

        // Get field values using ValueTuple's Item1, Item2, etc. fields
        var fields = GetTupleFields(tupleType);

        fixed (byte* destPtr = swiftDestSpan)
        {
            // Calculate offsets and marshal each element
            int currentOffset = 0;
            for (int i = 0; i < elementCount; i++)
            {
                var elementType = elementTypes[i];
                var elementValue = fields[i].GetValue(value);

                // Get element metadata to determine alignment
                var elementMetadata = GetTypeMetadataForType(elementType);
                var elementAlignment = elementMetadata.Alignment;
                var elementSize = (int)elementMetadata.Size;

                // Align the offset
                currentOffset = AlignOffset(currentOffset, elementAlignment);

                // Marshal the element directly using unsafe pointers
                MarshalElementToSwiftUnsafe(elementValue, elementType, destPtr + currentOffset);

                currentOffset += elementSize;
            }
        }

        return tupleSize;
    }

    /// <summary>
    /// Marshals a Swift tuple to a C# ValueTuple.
    /// Uses direct unsafe memory access for primitive types to avoid reflection overhead.
    /// </summary>
    /// <typeparam name="T">The ValueTuple type.</typeparam>
    /// <param name="swiftSource">The Swift memory source.</param>
    /// <returns>The marshalled ValueTuple.</returns>
    [RequiresDynamicCode("Tuple marshalling uses reflection for non-primitive element types")]
    [RequiresUnreferencedCode("Tuple marshalling requires access to ValueTuple constructors")]
    private static unsafe T MarshalTupleFromSwift<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields)] T>(IntPtr swiftSource)
    {
        var tupleType = typeof(T);
        var elementTypes = TypeMetadata.GetTupleElementTypes(tupleType);
        var elementCount = elementTypes.Length;

        // Get element values
        var elementValues = new object?[elementCount];
        int currentOffset = 0;

        for (int i = 0; i < elementCount; i++)
        {
            var elementType = elementTypes[i];

            // Get element metadata to determine alignment and size
            var elementMetadata = GetTypeMetadataForType(elementType);
            var elementAlignment = elementMetadata.Alignment;
            var elementSize = (int)elementMetadata.Size;

            // Align the offset
            currentOffset = AlignOffset(currentOffset, elementAlignment);

            // Marshal the element from Swift
            var elementPtr = IntPtr.Add(swiftSource, currentOffset);
            elementValues[i] = MarshalElementFromSwiftUnsafe(elementPtr, elementType, elementMetadata);

            currentOffset += elementSize;
        }

        // Pass typeof(T) directly so the closed-generic dispatch lines up with
        // T's [DynamicallyAccessedMembers(PublicConstructors)] annotation on
        // CreateValueTuple<T>. Using an unannotated `Type` local dropped the
        // annotation flow under ILC and stripped the closed-ctor metadata,
        // surfacing as "Could not find constructor for ValueTuple`N" on
        // NativeAOT for tuple returns from SwiftOptional<(T1, T2)> shapes.
        // Belt-and-braces preservation of System.ValueTuple`N constructors
        // lives in ILLink.Descriptors.xml (consumer-side) and the BindingTests
        // app's TrimmerRoots.xml (which is what actually keeps device green).
        return CreateValueTuple<T>(typeof(T), elementValues);
    }

    /// <summary>
    /// Gets the fields of a ValueTuple in order (Item1, Item2, etc.).
    /// </summary>
    [RequiresUnreferencedCode("ValueTuple field access")]
    private static FieldInfo[] GetTupleFields([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type tupleType)
    {
        var elementCount = tupleType.GetGenericArguments().Length;
        var fields = new FieldInfo[elementCount];

        for (int i = 0; i < elementCount; i++)
        {
            var fieldName = $"Item{i + 1}";
            fields[i] = tupleType.GetField(fieldName)
                ?? throw new InvalidOperationException($"Could not find field {fieldName} on {tupleType.Name}");
        }

        return fields;
    }

    /// <summary>
    /// Gets TypeMetadata for a runtime Type.
    /// </summary>
    [RequiresDynamicCode("Type metadata lookup uses reflection")]
    private static TypeMetadata GetTypeMetadataForType(Type type)
    {
        // Use reflection to call the generic TryGetTypeMetadata<T>
        var tryGetMethod = typeof(TypeMetadata).GetMethod(nameof(TypeMetadata.TryGetTypeMetadata), BindingFlags.Public | BindingFlags.Static)!;
        var genericMethod = tryGetMethod.MakeGenericMethod(type);

        var args = new object?[] { null };
        var success = (bool)genericMethod.Invoke(null, args)!;

        if (!success)
            throw new NotSupportedException($"Cannot get type metadata for {type.Name}");

        return ((TypeMetadata?)args[0])!.Value;
    }

    /// <summary>
    /// Aligns an offset to the given alignment.
    /// </summary>
    private static int AlignOffset(int offset, int alignment)
    {
        var remainder = offset % alignment;
        return remainder == 0 ? offset : offset + (alignment - remainder);
    }

    /// <summary>
    /// Marshals a single element value to Swift memory using direct pointer access.
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    private static unsafe void MarshalElementToSwiftUnsafe(object? value, Type elementType, byte* dest)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value), "Tuple element cannot be null");

        // Handle primitives directly without reflection
        if (elementType == typeof(bool))
        {
            *dest = (byte)((bool)value ? 1 : 0);
        }
        else if (elementType == typeof(byte))
        {
            *dest = (byte)value;
        }
        else if (elementType == typeof(sbyte))
        {
            *(sbyte*)dest = (sbyte)value;
        }
        else if (elementType == typeof(short))
        {
            *(short*)dest = (short)value;
        }
        else if (elementType == typeof(ushort))
        {
            *(ushort*)dest = (ushort)value;
        }
        else if (elementType == typeof(int))
        {
            *(int*)dest = (int)value;
        }
        else if (elementType == typeof(uint))
        {
            *(uint*)dest = (uint)value;
        }
        else if (elementType == typeof(long))
        {
            *(long*)dest = (long)value;
        }
        else if (elementType == typeof(ulong))
        {
            *(ulong*)dest = (ulong)value;
        }
        else if (elementType == typeof(float))
        {
            *(float*)dest = (float)value;
        }
        else if (elementType == typeof(double))
        {
            *(double*)dest = (double)value;
        }
        else if (elementType == typeof(nint))
        {
            *(nint*)dest = (nint)value;
        }
        else if (elementType == typeof(nuint))
        {
            *(nuint*)dest = (nuint)value;
        }
        else if (typeof(ISwiftObject).IsAssignableFrom(elementType))
        {
            // For ISwiftObject types, use MarshalToSwift through the interface
            var swiftObject = (ISwiftObject)value;
            var metadata = GetTypeMetadataForType(elementType);
            var span = new Span<byte>(dest, (int)metadata.Size);
            swiftObject.MarshalToSwift(ref span);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} to Swift");
        }
    }

    /// <summary>
    /// Marshals a single element from Swift memory using direct pointer access.
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "elementType comes from ValueTuple generic args which are preserved for tuple marshalling")]
    private static unsafe object? MarshalElementFromSwiftUnsafe(IntPtr source, Type elementType, TypeMetadata elementMetadata)
    {
        // Handle primitives directly without reflection
        if (elementType == typeof(bool))
        {
            return ((*(byte*)source) & 1) != 0;
        }
        else if (elementType == typeof(byte))
        {
            return *(byte*)source;
        }
        else if (elementType == typeof(sbyte))
        {
            return *(sbyte*)source;
        }
        else if (elementType == typeof(short))
        {
            return *(short*)source;
        }
        else if (elementType == typeof(ushort))
        {
            return *(ushort*)source;
        }
        else if (elementType == typeof(int))
        {
            return *(int*)source;
        }
        else if (elementType == typeof(uint))
        {
            return *(uint*)source;
        }
        else if (elementType == typeof(long))
        {
            return *(long*)source;
        }
        else if (elementType == typeof(ulong))
        {
            return *(ulong*)source;
        }
        else if (elementType == typeof(float))
        {
            return *(float*)source;
        }
        else if (elementType == typeof(double))
        {
            return *(double*)source;
        }
        else if (elementType == typeof(nint))
        {
            return *(nint*)source;
        }
        else if (elementType == typeof(nuint))
        {
            return *(nuint*)source;
        }
        else if (typeof(ISwiftObject).IsAssignableFrom(elementType))
        {
            // The tuple buffer this slot lives in is a borrowed read-by-value copy of a carrier payload
            // (e.g. Optional<(class, String)>): MarshalExtractedPayloadValue took a bitwise +0 copy of
            // the whole tuple, so the slot's reference is still the carrier's. Extract an INDEPENDENT
            // +1 per element so disposing the element wrapper never over-releases storage the carrier
            // still owns. COPY context — we never destroy the source slot.
            return ExtractCopiedElement(source, elementType, elementMetadata);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} from Swift");
        }
    }

    /// <summary>
    /// Builds NewFromPayload non-generically, preferring the NativeAOT-safe factory cache and falling
    /// back to reflection (Mono, or preserved NativeAOT types). The non-generic sibling of
    /// <see cref="MarshalFromSwift{T}"/>'s <c>ISwiftObject</c> dispatch, for tuple-element marshalling
    /// where the element type is only known as a <see cref="Type"/>.
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    private static object NewFromPayloadForType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type elementType,
        IntPtr payload)
    {
        var cached = NewFromPayloadDispatcher.TryCreate(elementType, payload);
        if (cached != null)
            return cached;
        return SwiftObjectReflectionHelper.InvokeNewFromPayload(elementType, payload);
    }

    /// <summary>
    /// The non-generic, per-element <c>Copy</c> sibling of <see cref="ExtractCopiedValue{T}"/>: extracts
    /// an <see cref="ISwiftObject"/> tuple element out of a <b>borrowed</b> slot into a wrapper that owns
    /// an <b>independent</b> reference, leaving the slot's reference (the carrier's) intact. Dispatch
    /// mirrors <see cref="ExtractCopiedValue{T}"/> / <see cref="MarshalExtractedPayloadValue{T}"/>, but
    /// keyed off the runtime <paramref name="elementType"/> and <paramref name="elementMetadata"/> rather
    /// than a generic parameter:
    /// <list type="bullet">
    /// <item><b>True Swift class</b> (not a value type, not <see cref="ISwiftStruct"/>, metadata
    /// <c>Kind == Class</c>): the slot word IS the instance pointer. Dereference it, take an independent
    /// <see cref="Arc.UnknownObjectRetain(System.IntPtr)"/> (<c>swift_unknownObjectRetain</c> dispatches by
    /// isa, so it is correct for both pure-Swift and <c>@objc</c>:NSObject-rooted classes; native-only
    /// <c>swift_retain</c> no-ops / over-releases on an NSObject subclass — audit P1-01), and build
    /// NewFromPayload from the pointer — a class's NewFromPayload wraps the pointer value directly, not
    /// the address holding it.</item>
    /// <item><b>Reference-backed non-class</b> (<see cref="ISwiftStruct"/>, bare-<see cref="ISwiftObject"/>
    /// SwiftUI value wrappers, <c>SwiftString</c>/<c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>):
    /// <c>InitializeWithCopy</c> into a temporary to take a fresh <c>+1</c>, then balance ARC across the
    /// adopt/copy/move <c>NewFromPayload</c> shapes exactly as <see cref="MarshalExtractedPayloadValue{T}"/>
    /// does — detected by comparing the wrapper's <c>SwiftHandle</c> to the temporary. We never destroy
    /// the source slot (the carrier still owns it).</item>
    /// <item><b>Value-type <see cref="ISwiftObject"/> struct / POD</b>: bitwise read straight from the
    /// slot, no ARC.</item>
    /// </list>
    /// </summary>
    [RequiresDynamicCode("Non-primitive element marshalling uses reflection")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "elementType comes from ValueTuple generic args which are preserved for tuple marshalling")]
    private static unsafe object? ExtractCopiedElement(IntPtr source, Type elementType, TypeMetadata elementMetadata)
    {
        // True Swift class: the slot word is the instance pointer; deref + independent ObjC-aware retain.
        // swift_unknownObjectRetain dispatches by isa, so it is correct for both pure-Swift and
        // @objc:NSObject-rooted classes; native-only swift_retain no-ops/over-releases on NSObject (P1-01).
        if (!elementType.IsValueType
            && !typeof(ISwiftStruct).IsAssignableFrom(elementType)
            && elementMetadata.IsValid
            && elementMetadata.Kind == TypeMetadataKind.Class)
        {
            IntPtr classPointer = *(IntPtr*)source;
            Arc.UnknownObjectRetain(classPointer);
            return NewFromPayloadForType(elementType, classPointer);
        }

        // Reference-backed non-class value: take a value-witness +1 into a temporary so the element
        // wrapper owns an independent reference, then balance ARC across adopt/copy/move. Value-type
        // ISwiftObject structs (SwiftHandle throws) are excluded and fall through to the bitwise read.
        if (!elementType.IsValueType
            && elementMetadata.IsValid
            && elementMetadata.ValueWitnessTable->IsNonPOD)
        {
            nuint size = elementMetadata.Size;
            byte* temp = (byte*)NativeMemory.AllocZeroed(size);
            elementMetadata.ValueWitnessTable->InitializeWithCopy(temp, (void*)source, elementMetadata);

            object wrapper;
            try
            {
                wrapper = NewFromPayloadForType(elementType, (IntPtr)temp);
            }
            catch
            {
                elementMetadata.ValueWitnessTable->Destroy(temp, elementMetadata);
                NativeMemory.Free(temp);
                throw;
            }

            if (wrapper is ISwiftObject swiftObj && swiftObj.SwiftHandle != (IntPtr)temp)
            {
                // COPY shape took its own +1, orphaning ours — destroy it. MOVE shape
                // (ISwiftMovesPayloadOnConstruction) transferred our +1 into the wrapper; only free.
                if (!typeof(ISwiftMovesPayloadOnConstruction).IsAssignableFrom(elementType))
                    elementMetadata.ValueWitnessTable->Destroy(temp, elementMetadata);
                NativeMemory.Free(temp);
            }
            // else ADOPT: the wrapper's SafeHandle owns the temporary (and its +1); leave it.
            return wrapper;
        }

        // Value-type ISwiftObject struct (read by value) / POD: bitwise read from the slot.
        return NewFromPayloadForType(elementType, source);
    }

    /// <summary>
    /// Creates a ValueTuple from an array of element values by reflectively
    /// invoking the closed <c>ValueTuple&lt;...&gt;</c> constructor.
    /// <para>
    /// The caller must pass <c>typeof(T)</c> rather than an intermediate
    /// <c>Type</c> local so the annotation on <c>T</c>
    /// (<c>PublicConstructors</c>) reaches the lookup. Routing through an
    /// unannotated <c>Type</c> local drops the data-flow annotation and ILC
    /// strips the closed-generic ctor metadata, surfacing as
    /// "Could not find constructor for ValueTuple`N" on NativeAOT. We do
    /// NOT additionally annotate the <c>tupleType</c> parameter — under
    /// .NET 10 ILC the doubled annotation regresses this exact test path
    /// (TestFirstNamedAnimalSome SIGTRAP); the descriptor below is the
    /// load-bearing preservation, the parameter stays plain <c>Type</c>.
    /// </para>
    /// <para>
    /// The operative NativeAOT preservation of
    /// <c>System.ValueTuple`1..`8</c> for every reachable closed
    /// instantiation comes from a trimmer descriptor. The package-side hint
    /// lives in this project's embedded <c>ILLink.Descriptors.xml</c>; ILC
    /// does not auto-discover embedded descriptors from referenced
    /// assemblies (only the IL trimmer does), so a NativeAOT consumer also
    /// has to pass the descriptor explicitly to ILC via an IlcArg item.
    /// The in-tree <c>BindingTests/RuntimeTestsApp/TrimmerRoots.xml</c>
    /// mirror plus the IlcArg in RuntimeTestsApp.csproj is what keeps the
    /// device gate green for this repo's tests; downstream NuGet consumers
    /// are tracked as a buildTransitive followup in src/docs/roadmap.md.
    /// </para>
    /// </summary>
    [RequiresUnreferencedCode("ValueTuple constructor access")]
    private static T CreateValueTuple<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Type tupleType, object?[] values)
    {
        // Look up the all-fields constructor (T1, T2, ...) and invoke it.
        var constructor = tupleType.GetConstructor(tupleType.GetGenericArguments())
            ?? throw new InvalidOperationException($"Could not find constructor for {tupleType.Name}");

        return (T)constructor.Invoke(values);
    }

    // NOTE: These helpers free Swift-allocated buffers with NativeMemory.Free (C free()).
    // Swift's UnsafeMutablePointer.allocate() uses swift_slowAlloc → malloc on Apple platforms,
    // so free() is the correct deallocator. Generated per-library code historically used
    // SBW_Free (which calls ptr.deallocate() → swift_slowDealloc → free()), but the shared
    // runtime can't reference a per-library P/Invoke. Both paths resolve to free().
    // This assumption holds for all supported targets (iOS/macOS ARM64).

    /// <summary>
    /// Reads a UTF-8 string from a Swift Utf8Slice stored at the given result pointer.
    /// The Utf8Slice's buffer is freed after reading. This replaces the inline 9-line
    /// decode-and-free pattern in generated bindings.
    /// </summary>
    /// <param name="resultPtr">Pointer to a Utf8Slice struct in native memory.</param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> if the slice is empty.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadUtf8Slice(IntPtr resultPtr)
    {
        var slice = *(Utf8Slice*)resultPtr;
        if (slice.Len == 0) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(slice.Ptr, (int)slice.Len) ?? string.Empty;
        }
        finally
        {
            NativeMemory.Free((void*)slice.Ptr);
        }
    }

    /// <summary>
    /// Reads a UTF-8 string from a Utf8Slice struct value. The slice's buffer is freed after
    /// reading. This overload is for property getters where the accessor returns a Utf8Slice
    /// by value (not via result pointer).
    /// </summary>
    /// <param name="slice">The Utf8Slice containing a pointer to UTF-8 bytes and their length.</param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> if the slice is empty.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadUtf8Slice(Utf8Slice slice)
    {
        if (slice.Len == 0) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(slice.Ptr, (int)slice.Len) ?? string.Empty;
        }
        finally
        {
            NativeMemory.Free((void*)slice.Ptr);
        }
    }

    /// <summary>
    /// Reads a Utf8Slice byte buffer as a managed byte array, then frees the buffer.
    /// Used by generated Codable JSON round-trip emitters: Swift writes encoded JSON
    /// bytes to a heap-allocated buffer and stores a Utf8Slice describing the buffer;
    /// C# copies the bytes out and releases the buffer.
    /// </summary>
    /// <param name="slicePtr">Pointer to a Utf8Slice struct in native memory.</param>
    /// <returns>A byte[] copy of the slice payload, or an empty array if length is zero.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe byte[] ReadUtf8SliceBytes(IntPtr slicePtr)
    {
        var slice = *(Utf8Slice*)slicePtr;
        if (slice.Len == 0) return Array.Empty<byte>();
        try
        {
            var bytes = new byte[(int)slice.Len];
            Marshal.Copy(slice.Ptr, bytes, 0, (int)slice.Len);
            return bytes;
        }
        finally
        {
            NativeMemory.Free((void*)slice.Ptr);
        }
    }

    /// <summary>
    /// Reads a Swift error description from a C string pointer and frees it.
    /// Returns "Unknown Swift error" if the pointer is null or the string is null.
    /// This replaces the inline error description extraction pattern in generated bindings.
    /// </summary>
    /// <param name="descPtr">Pointer to a null-terminated UTF-8 error description string,
    /// allocated by Swift (via SBW_GetErrorDescription). Freed after reading.</param>
    /// <returns>The error description string.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static unsafe string ReadErrorDescription(IntPtr descPtr)
    {
        if (descPtr == IntPtr.Zero)
            return "Unknown Swift error";
        try
        {
            return Marshal.PtrToStringUTF8(descPtr) ?? "Unknown Swift error";
        }
        finally
        {
            NativeMemory.Free((void*)descPtr);
        }
    }

    /// <summary>
    /// Handles an untyped Swift error by extracting the description message, releasing the error,
    /// and throwing a <see cref="SwiftException"/>. Used by generated bindings to replace inline
    /// error handling blocks.
    /// </summary>
    /// <param name="errorPtr">The Swift error pointer (from SwiftError.Value or @_cdecl out parameter).</param>
    /// <param name="descPtr">The error description pointer (from SBW_GetErrorDescription).</param>
    /// <param name="releaseError">Action to release the Swift error reference (SBW_ReleaseError).</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ThrowSwiftError(IntPtr errorPtr, IntPtr descPtr, Action<IntPtr> releaseError)
    {
        // Read + release BEFORE throw rather than wrapping throw in try/finally with a P/Invoke
        // in the finally. ReadErrorDescription frees descPtr inside its own try/finally; once it
        // returns, the only remaining native handle is errorPtr. Release it eagerly, then throw.
        // This avoids the "throw inside try/finally with a P/Invoke in the cleanup block" shape,
        // which interacts poorly with the maccatalyst-x64 Mono workload runtime's exception
        // unwinder under Rosetta (see src/docs/Future/upstream-issue-04-mono-catalyst-x64-instability.md).
        // SwiftException(string) cannot throw before reaching the throw statement, so we don't
        // need a finally to defend against an intermediate exception leaking errorPtr.
        var message = ReadErrorDescription(descPtr);
        releaseError(errorPtr);
        throw new SwiftException(message);
    }
}
