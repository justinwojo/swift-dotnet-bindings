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
/// Type-keyed registry of concrete <c>GetTypeMetadata</c> factories (Finding 32). Mirrors
/// <see cref="NewFromPayloadDispatcher"/>: generated module initializers register a concrete-typed
/// delegate for every emitted type (via <see cref="SwiftMarshal.RegisterSwiftObjectFactory{T}"/>) on
/// all runtimes, so the Mono / CoreCLR metadata lookups consult a typed delegate instead of a
/// name-matched reflection scan. The factory closes over the concrete type, so invoking it never
/// performs static-virtual dispatch in a shared-generic context (the Mono assertion the reflection
/// fallback exists to avoid). Genuinely-unregistered types — open Runtime generics such as
/// <c>SwiftArray&lt;Element&gt;</c> whose concrete instantiation cannot be registered from their
/// shared-generic call site — return false here and fall through to the reflective last resort.
/// </summary>
internal static class TypeMetadataDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<TypeMetadata>> _factories = new();

    /// <summary>
    /// Registers a metadata factory for a type. Safe to call multiple times — later calls are no-ops.
    /// </summary>
    internal static void Register(Type type, Func<TypeMetadata> factory)
    {
        _factories.TryAdd(type, factory);
    }

    /// <summary>
    /// Resolves a type's metadata through its registered factory. Returns false (and
    /// <see cref="TypeMetadata.Zero"/>) when no factory is registered, signalling the caller to fall
    /// back to the reflective last resort.
    /// </summary>
    internal static bool TryGet(Type type, out TypeMetadata metadata)
    {
        if (_factories.TryGetValue(type, out var factory))
        {
            metadata = factory();
            return true;
        }
        metadata = TypeMetadata.Zero;
        return false;
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
/// Registry mapping each <see cref="ISwiftObject"/> type to its declared
/// <see cref="PayloadConstructionSemantics"/>, populated by literal registrations from generated and
/// runtime <c>[ModuleInitializer]</c> code at assembly load time. This is the NativeAOT-safe path the
/// unconstrained marshal seam uses to read a type's payload-ownership contract without a static-virtual
/// call (which triggers the Mono JIT assertion jit-info.c:918 from shared generic code). Mirrors
/// <see cref="NewFromPayloadDispatcher"/>; reflection (<c>SwiftObjectReflectionHelper</c>) is the
/// backstop for unregistered reference-type implementers.
/// </summary>
internal static class PayloadSemanticsDispatcher
{
    private static readonly ConcurrentDictionary<Type, PayloadConstructionSemantics> _semantics = new();

    /// <summary>
    /// Registers a type's declared semantics. Called from generated/runtime <c>[ModuleInitializer]</c>
    /// code with a literal enum value (never a static-virtual property read). Safe to call repeatedly —
    /// subsequent calls are no-ops. Generic types register their open definition (<c>typeof(Foo&lt;&gt;)</c>):
    /// semantics are an invariant of the open generic, never of the closed type argument.
    /// </summary>
    internal static void Register(Type type, PayloadConstructionSemantics semantics)
    {
        _semantics.TryAdd(type, semantics);
    }

    /// <summary>
    /// Looks up a type's semantics: exact match first, then — for a closed generic — its open generic
    /// definition (so <c>SwiftArray&lt;FetchResult&gt;</c> resolves via the registered
    /// <c>SwiftArray&lt;&gt;</c>). Returns false on a miss so the caller can fall back to reflection.
    /// </summary>
    internal static bool TryGet(Type type, out PayloadConstructionSemantics semantics)
    {
        if (_semantics.TryGetValue(type, out semantics))
            return true;
        if (type.IsGenericType && _semantics.TryGetValue(type.GetGenericTypeDefinition(), out semantics))
            return true;
        return false;
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
            ReleasePathDiagnostics.OnWireDestroyMetadataUnavailable();
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
    /// <see cref="TypeMetadata.TryGetTypeMetadata{T}(out TypeMetadata?)"/> the method body already exercised, so no new
    /// generic type is forced.
    /// </summary>
    /// <param name="buffer">The wire buffer pointer to destroy. <c>IntPtr.Zero</c> is a no-op.</param>
    /// <param name="metadata">The Swift type metadata of the value occupying the buffer.</param>
    public static unsafe void DestroyWireBufferRetains(IntPtr buffer, TypeMetadata metadata)
    {
        if (buffer == IntPtr.Zero)
            return;
        if (!metadata.IsValid)
        {
            ReleasePathDiagnostics.OnWireDestroySkippedInvalid();
            return;
        }
        ReleasePathDiagnostics.OnWireDestroyEntered();
        metadata.ValueWitnessTable->Destroy((void*)buffer, metadata);
        ReleasePathDiagnostics.OnWireDestroyCompleted();
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
    /// use this from their <c>~Proxy()</c> finalizer. No-op on null buffer / invalid
    /// metadata.
    /// </summary>
    /// <param name="buffer">The wire buffer pointer to destroy. <c>IntPtr.Zero</c> is a no-op.</param>
    /// <param name="metadata">The Swift type metadata of the value occupying the buffer.</param>
    public static void DestroyWireBufferRetainsFinalizerSafe(IntPtr buffer, TypeMetadata metadata)
    {
        if (buffer == IntPtr.Zero)
            return;
        if (!metadata.IsValid)
        {
            ReleasePathDiagnostics.OnWireDestroySkippedInvalid();
            return;
        }
        ReleasePathDiagnostics.OnWireDestroyEntered();
        VwtDestroyTrampoline.Destroy(buffer, metadata.Handle);
        ReleasePathDiagnostics.OnWireDestroyCompleted();
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
    /// <item><b>Move-on-construction</b> (<see cref="PayloadConstructionSemantics.Move"/>,
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

        // Adopt (non-frozen struct, complex enum, bare-ISwiftObject SwiftUI wrapper) / Copy
        // (frozen-with-ref, SwiftArray/Dictionary/Set) reference-backed non-POD. Move (SwiftString)
        // and Inline (value-type / non-ISwiftObject) transfer or read their +1 via the bitwise read
        // below, so they are excluded here. Copy out an independent wrapper, THEN Destroy the slot's
        // original +1 — Destroy strictly after the copy so a throw leaves the slot intact (the caller's
        // exception path releases it). (sem is Adopt|Copy is exactly the former
        // "reference-backed && not bitwise-move-on-construction".)
        PayloadConstructionSemantics sem = GetPayloadSemantics<T>();
        if ((sem == PayloadConstructionSemantics.Adopt || sem == PayloadConstructionSemantics.Copy)
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
    /// The <b>borrowed</b> sibling of <see cref="MarshalMovedValueFromSlot{T}"/>: reads a
    /// <typeparamref name="T"/> out of a value slot the caller still owns, taking an <b>independent</b>
    /// reference and leaving the slot's own <c>+1</c> intact (no value-witness <c>Destroy</c>). Used for
    /// a generic type parameter in closure <i>argument</i> position: the Swift bridge specializes
    /// <c>T = UnsafeMutableRawPointer</c> and hands the C# callback the address of the (caller-owned)
    /// value buffer, which the user's closure only borrows for the duration of the synchronous call.
    /// <list type="bullet">
    /// <item><b>True class</b> (metadata <c>Kind == Class</c>): the slot holds the instance pointer;
    /// copy it out with an ObjC-aware retain so the wrapper handed to the user balances on
    /// <c>Dispose</c>/finalize — <see cref="MarshalBorrowedClassFromSlot{T}"/>.</item>
    /// <item><b>Reference-backed non-POD value</b> (Adopt/Copy/Move semantics, non-POD value-witness):
    /// take an <c>InitializeWithCopy</c> <c>+1</c> into a fresh wrapper via
    /// <see cref="MarshalExtractedPayloadValue{T}"/>, leaving the source slot's reference untouched.
    /// Move is included here (not in the bitwise fall-through) precisely because the slot stays
    /// caller-owned under a borrow — see the inline note at the call site.</item>
    /// <item><b>POD / inline</b>: a plain bitwise read carries no ARC reference, so it is simply read
    /// by value.</item>
    /// </list>
    /// This is the symmetric input-side counterpart of the moved read the generic-closure bridge already
    /// uses for its result slot, minus the consuming <c>Destroy</c> (the method, not the closure, owns
    /// the argument under noescape borrowing).
    /// </summary>
    /// <typeparam name="T">The generic type parameter occupying the slot.</typeparam>
    /// <param name="slot">Address of the caller-owned, initialized value slot. For classes it holds the object pointer.</param>
    /// <param name="metadata">Runtime metadata for <typeparamref name="T"/>: detects a true class and drives the borrowed copy-out.</param>
    public static unsafe T MarshalBorrowedValueFromSlot<T>(void* slot, TypeMetadata metadata)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T))
            && !typeof(T).IsValueType
            && !typeof(ISwiftStruct).IsAssignableFrom(typeof(T))
            && metadata.Kind == TypeMetadataKind.Class)
        {
            return MarshalBorrowedClassFromSlot<T>((IntPtr)slot);
        }

        // Move types (e.g. SwiftString, whose wrapper takes the bytes whole) must take an INDEPENDENT
        // InitializeWithCopy +1 here, exactly like Adopt/Copy. The plain MarshalFromSwift fall-through
        // does a consuming bitwise read — it transfers the slot's only +1 into the new wrapper without
        // retaining — which is correct for a moved (consuming) read but wrong for a borrow: the slot
        // stays caller-owned, so the caller's finally Destroy would then over-release the now-shared
        // reference (UAF on dispose/finalize). Route Move through the same copy-out as Adopt/Copy.
        PayloadConstructionSemantics sem = GetPayloadSemantics<T>();
        if ((sem == PayloadConstructionSemantics.Adopt
                || sem == PayloadConstructionSemantics.Copy
                || sem == PayloadConstructionSemantics.Move)
            && metadata.IsValid
            && metadata.ValueWitnessTable->IsNonPOD)
        {
            return MarshalExtractedPayloadValue<T>(slot, metadata.Size);
        }

        return MarshalFromSwift<T>((IntPtr)slot);
    }

    /// <summary>
    /// Copies a value of type <typeparamref name="T"/> out of a <b>borrowed</b> Swift value slot whose
    /// C# ABI carrier is <i>not</i> known to be a plain blittable value. This is the unconstrained
    /// entry point a protocol-proxy reverse-dispatch receiver uses for a parameter whose projected C#
    /// type is a managed wrapper (a non-frozen struct / complex enum wrapper, a frozen-with-references
    /// wrapper, <c>SwiftOptional&lt;T&gt;</c>, <c>SwiftResult&lt;…&gt;</c>, a Foundation value wrapper) or
    /// a narrow simple enum, and it takes the address of the slot — the generated Swift conformance
    /// makes a local copy (<c>var xCopy = x</c>, or a heap temporary) and passes <c>&amp;xCopy</c>, then
    /// deinitializes it once the receiver returns.
    /// <para>
    /// A raw <c>Unsafe.Read&lt;T&gt;</c> is wrong for both shapes. For a managed wrapper it reinterprets
    /// the slot's first machine word as a managed object reference — for a Swift enum with class
    /// payloads (VisionKit's <c>RecognizedItem</c> is the reported case) that word is a Swift heap
    /// pointer, so the very first use of the "wrapper" dereferences garbage. For a C# <c>enum : int</c>
    /// it reads four bytes where Swift stored the discriminator in one, so three bytes of the adjacent
    /// slot bleed into the case value.
    /// </para>
    /// <para>
    /// Dispatch mirrors the borrowed contract, never the moved one — the slot stays Swift's, so nothing
    /// here value-witness-<c>Destroy</c>s it:
    /// <list type="bullet">
    /// <item><b>True Swift class</b> (<see cref="ISwiftObject"/>, not a value type, not
    /// <see cref="ISwiftStruct"/>, metadata <c>Kind == Class</c>): the slot holds the instance pointer —
    /// dereference and take an ObjC-aware <c>+1</c> via <see cref="MarshalBorrowedClassFromSlot{T}"/>.</item>
    /// <item><b>Reference-backed managed wrapper</b> (every other <see cref="ISwiftObject"/> class —
    /// Adopt, Copy and Move alike): <see cref="MarshalExtractedPayloadValue{T}"/> copies the slot into a
    /// freshly-allocated buffer (value-witness <c>InitializeWithCopy</c> for a non-POD payload, a plain
    /// byte copy for a POD one) and balances the temporary against the wrapper's declared construction
    /// semantics. Routing an Adopt wrapper straight through <c>NewFromPayload</c> instead would make its
    /// SafeHandle adopt — and later free — Swift's own slot.</item>
    /// <item><b>Everything else</b> (primitives, blittable value types, existential containers, tuples,
    /// and C# enums): <see cref="MarshalFromSwift{T}"/>, whose core reads a simple enum at the Swift
    /// discriminator's width and every other value type by value.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <typeparam name="T">The ABI carrier type occupying the slot.</typeparam>
    /// <param name="slot">Address of the borrowed, initialized value slot (still owned by Swift).</param>
    /// <returns>The constructed value, owning an independent reference where one exists.</returns>
    /// <exception cref="SwiftRuntimeException">
    /// <typeparamref name="T"/> is a managed <see cref="ISwiftObject"/> wrapper whose Swift metadata
    /// cannot be resolved, so the value-witness copy that keeps the borrow honest cannot be performed.
    /// Failing here is deliberate: the alternatives silently alias or free memory Swift still owns.
    /// </exception>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Delegates to MarshalFromSwift / metadata resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Delegates to MarshalFromSwift / metadata resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Delegates to MarshalFromSwift / metadata resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "Delegates to MarshalFromSwift / metadata resolution")]
    public static unsafe T MarshalCopiedValueFromSlot<T>(IntPtr slot)
    {
        if (typeof(ISwiftObject).IsAssignableFrom(typeof(T)) && !typeof(T).IsValueType)
        {
            if (!TypeMetadata.TryGetTypeMetadata<T>(out var md) || !md.Value.IsValid)
                throw new SwiftRuntimeException(
                    $"Cannot copy a borrowed Swift value of type {typeof(T)} out of a reverse-dispatch slot: " +
                    "its Swift type metadata did not resolve, so the value-witness copy that leaves the " +
                    "borrowed slot intact cannot be performed.");

            if (md.Value.Kind == TypeMetadataKind.Class && !typeof(ISwiftStruct).IsAssignableFrom(typeof(T)))
                return MarshalBorrowedClassFromSlot<T>(slot);

            return MarshalExtractedPayloadValue<T>((void*)slot, md.Value.Size);
        }

        return MarshalFromSwift<T>(slot);
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
    /// <b>Cleanup</b> follows the wrapper type's <b>declared</b> <see cref="PayloadConstructionSemantics"/>
    /// (read via <see cref="GetPayloadSemantics{T}"/>; see <see cref="CleanupTemporary"/>):
    /// <list type="bullet">
    /// <item><b>Adopt</b> (non-frozen structs, complex enums, the SwiftUI value wrappers): the wrapper's
    /// SafeHandle adopted the temporary pointer directly — leave it; the wrapper owns the temporary and its <c>+1</c>.</item>
    /// <item><b>Copy</b> (frozen-projected-as-class structs, <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>/etc.):
    /// the wrapper allocated its own buffer and <c>InitializeWithCopy</c>d into it, taking a fresh
    /// <c>+1</c> — the temporary's <c>+1</c> is orphaned, so value-witness <c>Destroy</c> it, then free the dead buffer.</item>
    /// <item><b>Move</b> (<c>SwiftString</c>): the wrapper allocated its own buffer and <i>bitwise</i>-copied
    /// the temporary, transferring our <c>+1</c> into it — destroying would over-release the now-shared
    /// reference, so only free the dead buffer.</item>
    /// <item><b>Inline</b>: the wrapper read the value by value and never adopted a buffer — only free the temporary.</item>
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

        // The declared construction contract drives both the retain decision and the cleanup, replacing
        // the former post-hoc SwiftHandle-vs-temp comparison + bitwise-move-on-construction marker probe.
        // Adopt/Copy/Move (reference-backed: every ISwiftStruct plus the bare-ISwiftObject SwiftUI
        // wrappers) take a value-witness +1 into the temporary so the wrapper does not share the
        // carrier's only reference. Inline (non-ISwiftObject values — existential containers, primitives,
        // tuples — and frozen blittable value-type ISwiftObject structs whose NewFromPayload reads
        // *(T*)handle by value) takes a plain bitwise copy. True Swift classes never reach here — the
        // callers' class fast path (metadata Kind == Class) handles them first.
        PayloadConstructionSemantics sem = GetPayloadSemantics<T>();

        bool tempRetained = false;
        TypeMetadata metadata = default;
        if (sem != PayloadConstructionSemantics.Inline
            && TypeMetadata.TryGetTypeMetadata<T>(out var md)
            && md.Value.IsValid
            && md.Value.ValueWitnessTable->IsNonPOD)
        {
            metadata = md.Value;
            metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, source, metadata);
            tempRetained = true;
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
            if (tempRetained)
                metadata.ValueWitnessTable->Destroy(heapCopy, metadata);
            NativeMemory.Free(heapCopy);
            throw;
        }

        CleanupTemporary(heapCopy, sem, metadata, tempRetained);
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
    /// <c>swift_retain</c> no-ops/over-releases on an NSObject subclass), and marshal the
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
    /// <c>swift_retain</c> is a no-op/over-release on an NSObject subclass), and build
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
    /// This is the receiver for a generic-parameter indirect return specialized to a class conformer:
    /// the Swift wrapper does <c>resultPtr.initializeMemory(as: (C).self, repeating:
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
    /// <see cref="MarshalCallbackArg{T}"/> path for class parameters, whose
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
    /// The <c>NewFromPayload</c> body for a generated class whose C# base is an
    /// <c>NSObject</c>-derived type (an <c>@objc</c>/NSObject-rooted Swift class projected onto
    /// <c>Foundation.NSObject</c>, <c>UIKit.UIViewController</c>, …). Returns the managed peer
    /// already registered for <paramref name="handle"/> when there is one, and only otherwise
    /// calls <paramref name="constructPeer"/> to build a fresh one.
    /// <para>
    /// A native NSObject may have at most ONE managed peer: the Apple bindings keep a
    /// handle→peer map, and constructing a second wrapper over a native object that already has
    /// one repoints that map. Without this lookup every reverse-dispatch callback that hands a
    /// receiver back to C# (a delegate method whose first parameter is the object the user
    /// created) built a brand-new <c>UIViewController</c>-derived peer per invocation: reference
    /// identity against the user's own instance was lost, any state on the user's subclass was
    /// invisible, and at callback rates it allocated a peer many times a second. The same
    /// mechanism applies in the return direction, since a returned class routes through
    /// <see cref="MarshalFromSwiftObject{T}"/> into the same <c>NewFromPayload</c>.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> <c>NewFromPayload</c>'s contract is that <paramref name="handle"/>
    /// carries a <c>+1</c> the resulting wrapper takes over — the borrowed-slot readers
    /// (<see cref="MarshalBorrowedClassFromSlot{T}"/> and friends) take that retain immediately
    /// before calling in, and the owned readers transfer the carrier's. The construction branch
    /// keeps that unchanged: the peer's constructor absorbs the <c>+1</c>. The reuse branch has
    /// no new owner to absorb it — the existing peer already owns its own reference — so it hands
    /// the <c>+1</c> straight back with the ObjC-aware release that mirrors the caller's retain.
    /// </para>
    /// <para>
    /// A registered peer of an unrelated managed type cannot be returned as
    /// <typeparamref name="T"/>, so that case falls through to construction — the pre-existing
    /// behavior. A peer of a type <i>derived</i> from <typeparamref name="T"/> is returned as-is,
    /// which is strictly better than flattening the user's subclass to its base.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The generated NSObject-rooted wrapper type.</typeparam>
    /// <param name="handle">The Swift/ObjC instance pointer, carrying the <c>+1</c> described above.</param>
    /// <param name="constructPeer">Builds a fresh peer that adopts the <c>+1</c>. Invoked only when no peer is registered.</param>
    /// <returns>The registered peer, or a newly constructed one.</returns>
    public static T ObjCPeerFromPayload<T>(IntPtr handle, Func<IntPtr, T> constructPeer) where T : class
    {
#if IOS || TVOS || MACCATALYST || MACOS
        if (handle != IntPtr.Zero && ObjCRuntime.Runtime.TryGetNSObject(handle) is T existingPeer)
        {
            Arc.UnknownObjectRelease(handle);
            return existingPeer;
        }
#endif
        return constructPeer(handle);
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
    /// Pre-registers a type's concrete factories so the runtime can create instances and resolve
    /// type metadata without reflection. Called by generated [ModuleInitializer] code (and the
    /// runtime's own resolver for its concrete ISwiftObject types) at assembly load time. T is always
    /// a concrete type here, so both registered delegates close over a monomorphic static-abstract
    /// call — safe to create and later invoke on Mono, unlike a shared-generic static-virtual dispatch.
    /// Both registrations are deferred (the metadata accessor is not invoked at registration time), so
    /// registering a generic type's metadata factory cannot trip the Swift-runtime SIGSEGV that calling
    /// the accessor during module init can.
    /// </summary>
    /// <typeparam name="T">The ISwiftObject type to register.</typeparam>
    public static void RegisterSwiftObjectFactory<T>() where T : ISwiftObject
    {
        NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
        TypeMetadataDispatcher.Register(typeof(T), () => T.GetTypeMetadata());
    }

    /// <summary>
    /// Pre-registers a type's declared <see cref="PayloadConstructionSemantics"/> so the unconstrained
    /// marshal seam can read its payload-ownership contract without a static-virtual call. Called by
    /// generated and runtime <c>[ModuleInitializer]</c> code with a <b>literal</b> enum value (matching
    /// the type's <c>static PayloadConstructionSemantics</c> declaration) — never a property read on a
    /// generic parameter, which would re-introduce the Mono static-virtual hazard. Generic types pass
    /// their open definition (<c>typeof(Foo&lt;&gt;)</c>).
    /// </summary>
    /// <param name="type">The ISwiftObject implementer (or its open generic definition).</param>
    /// <param name="semantics">The literal semantics the type declares.</param>
    public static void RegisterPayloadSemantics(Type type, PayloadConstructionSemantics semantics)
    {
        PayloadSemanticsDispatcher.Register(type, semantics);
    }

    /// <summary>
    /// Resolves the declared payload-construction semantics for an unconstrained marshal-seam type
    /// parameter. Non-<see cref="ISwiftObject"/> payloads (primitives, tuples, existential containers)
    /// and value-type <see cref="ISwiftObject"/> structs both read by value, so they short-circuit to
    /// <see cref="PayloadConstructionSemantics.Inline"/> without a lookup — making a cache miss possible
    /// only for a reference-type <see cref="ISwiftObject"/>, exactly the comprehensively-registered set
    /// (with a reflection backstop). See <see cref="GetPayloadSemanticsForType"/> for the non-generic sibling.
    /// </summary>
    /// <typeparam name="T">The seam type parameter (may be any marshalled type, not just ISwiftObject).</typeparam>
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; the static PayloadConstructionSemantics member is preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    internal static PayloadConstructionSemantics GetPayloadSemantics<T>()
        => GetPayloadSemanticsForType(typeof(T));

    /// <summary>
    /// Non-generic resolution of declared payload-construction semantics, keyed off a runtime
    /// <see cref="Type"/> (for tuple-element marshalling where the element type is only a <see cref="Type"/>).
    /// Short-circuits non-<see cref="ISwiftObject"/> and value-type to <see cref="PayloadConstructionSemantics.Inline"/>,
    /// then the by-Type cache (exact, then open-generic), then the reflection backstop (which registers the
    /// resolved value and throws loudly on a genuine miss rather than guessing).
    /// </summary>
    internal static PayloadConstructionSemantics GetPayloadSemanticsForType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type)
    {
        if (!typeof(ISwiftObject).IsAssignableFrom(type))
            return PayloadConstructionSemantics.Inline;   // primitives / tuples / existential containers — read by value
        if (type.IsValueType)
            return PayloadConstructionSemantics.Inline;   // value-type ISwiftObject struct — read by value; SwiftHandle throws
        if (PayloadSemanticsDispatcher.TryGet(type, out var sem))
            return sem;                                   // reference type: Adopt / Copy / Move from the literal registry

        // Backstop for a reference-type ISwiftObject not pre-registered (Mono reflection; NativeAOT
        // relies on registration but the preserved static member resolves here too). Cache + return.
        sem = SwiftObjectReflectionHelper.InvokePayloadConstructionSemantics(type);
        PayloadSemanticsDispatcher.Register(type, sem);
        return sem;
    }

    /// <summary>
    /// Frees the temporary buffer a payload extraction constructed a wrapper from, balancing Swift ARC
    /// per the wrapper's <b>declared</b> <see cref="PayloadConstructionSemantics"/> — the single seam that
    /// replaces the former post-hoc detection (comparing the wrapper's <c>SwiftHandle</c> to the temp,
    /// plus probing a dedicated bitwise-move-on-construction marker). The construct step differs by
    /// caller (generic <c>MarshalFromSwift&lt;T&gt;</c> vs non-generic <c>NewFromPayloadForType</c>) but the
    /// cleanup is identical, so both extraction sites share this.
    /// <list type="bullet">
    /// <item><b>Adopt</b>: the wrapper's SafeHandle adopted <paramref name="temp"/> (and its <c>+1</c>) — leave it.</item>
    /// <item><b>Copy</b>: the wrapper made its own <c>+1</c> copy, orphaning ours — value-witness
    /// <c>Destroy</c> <paramref name="temp"/> (if <paramref name="tempRetained"/>), then free the dead buffer.</item>
    /// <item><b>Move</b>: the wrapper bitwise-transferred our <c>+1</c> — only free the dead buffer (a
    /// <c>Destroy</c> would over-release the now-shared reference).</item>
    /// <item><b>Inline</b>: the wrapper read the value by value (never touched <paramref name="temp"/> as a
    /// handle) — only free the buffer. <paramref name="tempRetained"/> is always false here.</item>
    /// </list>
    /// </summary>
    /// <param name="temp">The temporary buffer the wrapper was constructed from.</param>
    /// <param name="sem">The wrapper type's declared construction semantics.</param>
    /// <param name="metadata">Value-witness metadata for the payload (used only for the Copy <c>Destroy</c>).</param>
    /// <param name="tempRetained">True if the caller took a value-witness <c>+1</c> into <paramref name="temp"/>.</param>
    private static unsafe void CleanupTemporary(byte* temp, PayloadConstructionSemantics sem, TypeMetadata metadata, bool tempRetained)
    {
        switch (sem)
        {
            case PayloadConstructionSemantics.Adopt:
                // The wrapper's SafeHandle owns temp and its +1 — leave it for the wrapper's dispose/finalize.
                break;
            case PayloadConstructionSemantics.Copy:
                if (tempRetained)
                    metadata.ValueWitnessTable->Destroy(temp, metadata);
                NativeMemory.Free(temp);
                break;
            case PayloadConstructionSemantics.Move:
            case PayloadConstructionSemantics.Inline:
                NativeMemory.Free(temp);
                break;
        }
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
    /// Declares where the Swift protocol-conformance descriptor for a (type, protocol) pair lives,
    /// for conforming types that cannot implement <see cref="ISwiftObject"/>. Called by generated
    /// [ModuleInitializer] code alongside <see cref="TypeMetadata.RegisterMetadata"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="RegisterConformanceFactory{TType, TProtocol}"/> and
    /// <see cref="RegisterWitnessTable{TType, TProtocol}"/> lanes are both constrained to
    /// <see cref="ISwiftObject"/>, which a C# <c>enum</c> can never satisfy — so a payload-less
    /// raw-value Swift enum, projected as a plain C# enum, had no way to declare a conformance
    /// even though its descriptor symbol is exported. This is that lane: it takes
    /// <see cref="Type"/> operands instead of type parameters (so a C# enum can be named),
    /// registers only a symbol location, and defers the load until the conformance is first
    /// needed. Consumers reach it through
    /// <see cref="ProtocolConformanceDescriptor.TryGet{TType, TProtocol}"/>, so every witness-table
    /// resolution — <c>SwiftSet</c>, <c>SwiftDictionary</c> keys, existential boxing — sees it.
    /// </remarks>
    /// <param name="conformingType">The C# type standing in for the Swift conforming type.</param>
    /// <param name="protocolType">The C# marker interface standing in for the Swift protocol
    /// (e.g. <c>typeof(ISwiftHashable)</c>).</param>
    /// <param name="libraryName">The library exporting the conformance-descriptor symbol.</param>
    /// <param name="symbolName">The mangled conformance-descriptor symbol.</param>
    public static void RegisterConformanceSymbol(Type conformingType, Type protocolType, string libraryName, string symbolName)
        => ConformanceSymbolRegistry.Register(conformingType, protocolType, libraryName, symbolName);

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
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; the type is preserved for consumers by the shipped ILLink.Descriptors.xml — Swift.Runtime's own embedded+rooted descriptor for Runtime-owned types (closed ValueTuple generics, SwiftArray, etc.) and the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    public static T MarshalFromSwiftObject<T>(IntPtr swiftSource) where T : ISwiftObject
    {
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            // Register factory so unconstrained callers (MarshalFromSwift<T>) can use it later.
            NewFromPayloadDispatcher.Register(typeof(T), handle => (object)T.NewFromPayload(handle));
            return (T)DirectNewFromPayload<T>(swiftSource);
        }

        // Mono / CoreCLR: consult the factory cache first (Finding 32). Generated module
        // initializers register a concrete-typed factory for every emitted type on ALL
        // runtimes (RegisterSwiftObjectFactory<ConcreteType>), so the common case is a
        // dictionary lookup + delegate invoke — no per-call reflection. We must NOT register
        // here on Mono: the registration lambda contains the static-abstract T.NewFromPayload
        // call, which would compile in this possibly-shared generic body and assert on Mono
        // (jit-info.c:918). Reflection remains the cold fallback on a cache miss.
        var cached = NewFromPayloadDispatcher.TryCreate(typeof(T), swiftSource);
        if (cached != null)
            return (T)cached;
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
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; the type is preserved for consumers by the shipped ILLink.Descriptors.xml — Swift.Runtime's own embedded+rooted descriptor for Runtime-owned types (closed ValueTuple generics, SwiftArray, etc.) and the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    [UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "RunClassConstructor is a NativeAOT fallback in try-catch; type is always an ISwiftObject whose static constructor is preserved")]
    public static T MarshalFromSwift<T>(IntPtr swiftSource)
    {
        return MarshalFromSwiftCore<T>(swiftSource);
    }

    /// <summary>
    /// Marshals a <b>borrowed</b> (+0) Swift reference handed to a closure/callback into a C# wrapper,
    /// dispatching on the wrapper type's declared <see cref="PayloadConstructionSemantics"/> so each
    /// ownership shape balances ARC correctly. Replaces the former blanket-suppress
    /// borrowed-marshal, whose always-suppress strategy <b>leaked</b> a <c>Copy</c> wrapper
    /// (<c>SwiftResult</c>/<c>SwiftArray</c>/…): its <c>NewFromPayload</c> takes its OWN <c>+1</c> via
    /// <c>InitializeWithCopy</c> into an owned buffer, so suppressing the SafeHandle finalizer foreclosed
    /// the value-witness <c>Destroy</c> of that owned copy → a leaked <c>+1</c> + native buffer per call.
    /// <list type="bullet">
    /// <item><b>True class</b> (metadata <c>Kind == Class</c>): take an ObjC-aware <c>+1</c> into an OWNING
    /// wrapper (<see cref="MarshalBorrowedClassFromSwift{T}"/>) — an explicit <c>Dispose</c> in the callback
    /// and the finalizer both balance it. Also catches a generic-closure-bridge <c>T</c> that closes as a
    /// class, which the emitter's <c>IsClassType</c> split could not see at generation time.</item>
    /// <item><b>Copy</b>: construct OWNING (<b>no</b> suppress). The ctor's <c>InitializeWithCopy</c> takes
    /// an independent <c>+1</c>; the borrowed <c>+1</c> stays with Swift; the wrapper's SafeHandle Destroys
    /// its own copy. This is the leak fix.</item>
    /// <item><b>Adopt</b> (borrowed pointer adopted by the SafeHandle): suppress the payload finalizer —
    /// the adopted memory is Swift's outright, so neither the free nor the Destroy may run (the
    /// read-and-discard contract).</item>
    /// <item><b>Move</b> (borrowed <c>+0</c> bitwise-transferred into a wrapper-allocated container):
    /// call <see cref="ISwiftObject.ConsumePayloadBuffer"/> — cleanup frees the wrapper's OWN container
    /// but never value-witness-destroys the borrowed value. The former blanket suppression foreclosed
    /// the container free too, leaking the wrapper's allocation (e.g. <c>SwiftString</c>'s 16-byte
    /// buffer) on every callback invocation.</item>
    /// <item><b>Inline</b>: read by value (<c>*(T*)ptr</c> / existential container words) — self-contained,
    /// nothing to suppress.</item>
    /// </list>
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Delegates to MarshalFromSwift / metadata + semantics resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Delegates to MarshalFromSwift / metadata + semantics resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Delegates to MarshalFromSwift / metadata + semantics resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "Delegates to MarshalFromSwift / metadata + semantics resolution")]
    [UnconditionalSuppressMessage("Trimming", "IL2059", Justification = "Delegates to MarshalFromSwift / metadata + semantics resolution")]
    public static T MarshalCallbackArg<T>(IntPtr swiftSource)
    {
        // True Swift class: a borrowed (+0) class pointer needs an ObjC-aware +1 into an owning wrapper.
        if (TypeMetadata.TryGetTypeMetadata<T>(out var classMd)
            && classMd.Value.IsValid
            && classMd.Value.Kind == TypeMetadataKind.Class)
            return MarshalBorrowedClassFromSwift<T>(swiftSource);

        PayloadConstructionSemantics sem = GetPayloadSemantics<T>();
        if (sem == PayloadConstructionSemantics.Copy)
        {
            // Owning, NO suppress: the ctor InitializeWithCopy-s its own +1; the SafeHandle Destroys it.
            return MarshalFromSwift<T>(swiftSource);
        }

        var obj = MarshalFromSwift<T>(swiftSource);
        if (obj is ISwiftObject swiftObj)
        {
            if (sem == PayloadConstructionSemantics.Move)
            {
                // Move wrapper: it bitwise-transferred the borrowed +0 words into a container buffer
                // the WRAPPER itself allocated, so it owns that container but not the value inside it.
                // ConsumePayloadBuffer keeps the container free alive (Dispose/finalizer reclaim the
                // wrapper's own allocation) while dropping only the value-witness Destroy — the old
                // blanket finalizer suppression foreclosed the free too and leaked the container per
                // callback invocation. The DIM default falls back to suppress for Move types with no
                // separable container.
                swiftObj.ConsumePayloadBuffer();
            }
            else if (sem == PayloadConstructionSemantics.Adopt)
            {
                // Adopt wrapper: its SafeHandle adopted the borrowed pointer itself — that memory is
                // Swift's outright, so both the free and the Destroy must be suppressed.
                // SuppressPayloadFinalizer is a non-reflective DIM; its default is a no-op for types
                // with no separately-finalizable payload.
                GC.SuppressFinalize(obj);
                swiftObj.SuppressPayloadFinalizer();
            }
        }
        return obj;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tuple marshalling path only; non-tuple paths are AOT-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Tuple marshalling path only; non-tuple paths are trim-safe")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; the type is preserved for consumers by the shipped ILLink.Descriptors.xml — Swift.Runtime's own embedded+rooted descriptor for Runtime-owned types (closed ValueTuple generics, SwiftArray, etc.) and the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
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
            // for types kept by the shipped ILLink.Descriptors.xml — Swift.Runtime's own
            // embedded+rooted descriptor for Runtime-owned types, plus the per-binding
            // descriptor delivered in buildTransitive/ for generated types. (The
            // BindingTests app's TrimmerRoots.xml is a test-only mirror, not shipped.)
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

        // Handle delegate types (closures)
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
                MarshalElementToSwiftUnsafe(elementValue, elementType, elementMetadata, destPtr + currentOffset);

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
        // Belt-and-braces preservation of System.ValueTuple`N constructors for
        // CONSUMERS lives in Swift.Runtime's shipped ILLink.Descriptors.xml,
        // delivered + rooted to ILC by build/SwiftBindings.Runtime.targets. The
        // BindingTests app's TrimmerRoots.xml is only the in-repo mirror that keeps
        // this repo's device gate green; it is never shipped to consumers.
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
    /// Gets TypeMetadata for a runtime Type. Resolves by Type rather than closing
    /// <c>TryGetTypeMetadata&lt;T&gt;</c> reflectively — <c>MakeGenericMethod</c> is unsupported on
    /// NativeAOT, and this runs per tuple element under reverse-dispatch receivers whose
    /// UnmanagedCallersOnly frames turn the resulting NotSupportedException into a process abort.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Element types come from a ValueTuple's own generic arguments; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    private static TypeMetadata GetTypeMetadataForType(Type type)
    {
        if (!TypeMetadata.TryGetTypeMetadata(type, out var result, out var resolutionFailure))
        {
            throw resolutionFailure is null
                ? new NotSupportedException($"Cannot get type metadata for {type.Name}")
                : new NotSupportedException($"Cannot get type metadata for {type.Name}", resolutionFailure);
        }

        return result.Value;
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
    private static unsafe void MarshalElementToSwiftUnsafe(object? value, Type elementType, TypeMetadata elementMetadata, byte* dest)
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
        else if (elementType.IsEnum)
        {
            // The write-direction counterpart of the read arm: a payload-less Swift enum is a plain
            // C# enum, matches no primitive arm, and is not an ISwiftObject. Writing the C# width
            // would stomp the bytes of whichever tuple element Swift packed next to a narrow
            // discriminator, so write exactly the width Swift stores.
            ulong caseValue = Enum.GetUnderlyingType(elementType) == typeof(ulong)
                ? Convert.ToUInt64(value)
                : unchecked((ulong)Convert.ToInt64(value));
            WriteDiscriminator(caseValue, elementMetadata, dest);
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} to Swift");
        }
    }

    /// <summary>
    /// Writes a payload-less enum's case discriminator into Swift memory at the width Swift stores
    /// it in, mirroring <see cref="ReadDiscriminator"/>.
    /// </summary>
    private static unsafe void WriteDiscriminator(ulong caseValue, TypeMetadata elementMetadata, byte* dest)
    {
        int swiftSize = elementMetadata.IsValid ? (int)elementMetadata.Size : 1;

        switch (swiftSize)
        {
            case 1: dest[0] = (byte)caseValue; return;
            case 2: *(ushort*)dest = (ushort)caseValue; return;
            case 4: *(uint*)dest = (uint)caseValue; return;
            case 8: *(ulong*)dest = caseValue; return;
        }

        int byteCount = Math.Min(swiftSize <= 0 ? 1 : swiftSize, sizeof(ulong));
        for (int i = 0; i < byteCount; i++)
            dest[i] = (byte)(caseValue >> (8 * i));
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
        else if (elementType.IsEnum)
        {
            // A payload-less Swift enum projects to a plain C# enum, so it matches none of the
            // primitive arms above (typeof(int) != typeof(SomeEnum)) and is not an ISwiftObject —
            // complex/payload enums are, and take the extract path above. Its Swift storage is the
            // narrowest discriminator that fits the case count (1 byte for <=256 cases, 2 for
            // <=65536, ...), while C# gives it a 4-byte underlying type, so a bitwise read of the
            // C# width would pull in the NEIGHBOURING tuple element's bytes. Read exactly the
            // Swift-reported width; a discriminator is an unsigned case index, so zero-extend.
            return Enum.ToObject(elementType, ReadDiscriminator(source, elementMetadata));
        }
        else
        {
            throw new NotSupportedException($"Cannot marshal tuple element type {elementType.Name} from Swift");
        }
    }

    /// <summary>
    /// Reads a payload-less enum's case discriminator out of Swift memory using the width Swift
    /// actually stores it in, zero-extended. Metadata is required to know that width; without it
    /// the safest read is the narrowest one, which never over-reads a neighbouring field.
    /// </summary>
    private static unsafe ulong ReadDiscriminator(IntPtr source, TypeMetadata elementMetadata)
    {
        int swiftSize = elementMetadata.IsValid ? (int)elementMetadata.Size : 1;

        switch (swiftSize)
        {
            case 1: return ((byte*)source)[0];
            case 2: return *(ushort*)source;
            case 4: return *(uint*)source;
            case 8: return *(ulong*)source;
        }

        // Any other width (a 3-byte discriminator is legal Swift) is assembled little-endian a
        // byte at a time rather than guessed at, so the read stays inside the element.
        ulong value = 0;
        int byteCount = Math.Min(swiftSize <= 0 ? 1 : swiftSize, sizeof(ulong));
        for (int i = 0; i < byteCount; i++)
            value |= (ulong)((byte*)source)[i] << (8 * i);

        return value;
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
    /// <c>swift_retain</c> no-ops / over-releases on an NSObject subclass), and build
    /// NewFromPayload from the pointer — a class's NewFromPayload wraps the pointer value directly, not
    /// the address holding it.</item>
    /// <item><b>Reference-backed non-class</b> (<see cref="ISwiftStruct"/>, bare-<see cref="ISwiftObject"/>
    /// SwiftUI value wrappers, <c>SwiftString</c>/<c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>):
    /// <c>InitializeWithCopy</c> into a temporary to take a fresh <c>+1</c>, then balance ARC across the
    /// adopt/copy/move <c>NewFromPayload</c> shapes exactly as <see cref="MarshalExtractedPayloadValue{T}"/>
    /// does — driven by the element type's declared <see cref="PayloadConstructionSemantics"/>. We never
    /// destroy the source slot (the carrier still owns it).</item>
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
        // @objc:NSObject-rooted classes; native-only swift_retain no-ops/over-releases on NSObject.
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

            // Reference-backed (non-class) element: Adopt leaves temp, Copy destroys then frees, Move
            // frees only — the declared contract, replacing the former SwiftHandle-vs-temp comparison +
            // bitwise-move-on-construction marker probe. (We always took a +1 via InitializeWithCopy above.)
            PayloadConstructionSemantics sem = GetPayloadSemanticsForType(elementType);
            CleanupTemporary(temp, sem, elementMetadata, tempRetained: true);
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
    /// instantiation comes from a trimmer descriptor. It lives in this
    /// project's <c>ILLink.Descriptors.xml</c> (embedded for the IL-trimmer
    /// auto-discovery path). ILC does not auto-discover embedded descriptors
    /// from referenced assemblies, so the descriptor must be passed to ILC
    /// explicitly via an IlcArg item — and for downstream NuGet consumers it
    /// is: <c>build/SwiftBindings.Runtime.targets</c> ships the loose copy in
    /// <c>buildTransitive/</c> and injects the PublishAot-gated
    /// <c>--descriptor:</c> IlcArg + <c>TrimmerRootDescriptor</c> into the
    /// consuming app's publish, so a NativeAOT consumer keeps the closed
    /// ValueTuple ctors with no action of their own. The in-tree
    /// <c>BindingTests/RuntimeTestsApp/TrimmerRoots.xml</c> mirror plus the
    /// IlcArg in RuntimeTestsApp.csproj is only what keeps this repo's own
    /// device gate green; it is a test artifact, never shipped to consumers.
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
    /// Handles an untyped Swift error by extracting the description message and throwing a
    /// <see cref="SwiftException"/> that carries the LIVE (still-retained) error box, so a consumer
    /// can recover error identity through <see cref="SwiftException.ErrorHandle"/> instead of only
    /// the flattened message. Used by generated bindings to replace inline error handling blocks.
    /// </summary>
    /// <param name="errorPtr">The Swift error pointer (from SwiftError.Value or @_cdecl out parameter).</param>
    /// <param name="descPtr">The error description pointer (from SBW_GetErrorDescription).</param>
    /// <param name="releaseError">Action to release the Swift error reference (SBW_ReleaseError).</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ThrowSwiftError(IntPtr errorPtr, IntPtr descPtr, Action<IntPtr> releaseError)
    {
        // Ownership of errorPtr transfers to the thrown SwiftException, which releases it (via
        // releaseError) when finalized, under the process-exit guard. The throw path itself runs NO
        // P/Invoke: ReadErrorDescription frees descPtr inside its own try/finally, and the
        // SwiftException constructor only stores fields. This is strictly safer than the prior
        // eager releaseError-then-throw for the maccatalyst-x64 Mono workload runtime's exception
        // unwinder under Rosetta — there is no release in a finally around the throw, and now no
        // release on the throw path at all (Mono maccatalyst-x64 exception unwinding instability).
        var message = ReadErrorDescription(descPtr);
        throw new SwiftException(message, errorPtr, releaseError);
    }
}
