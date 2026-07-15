// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Swift.Runtime;

/// <summary>
/// Represents a type that can be marshaled into Swift
/// </summary>
public interface ISwiftObject : IDisposable
{
    /// <summary>
    /// Returns the TypeMetadata for this object
    /// </summary>
    /// <returns>A type metadata object for the type.</returns>
    public static abstract TypeMetadata GetTypeMetadata();

    /// <summary>
    /// Creates a new Swift object from a given payload
    /// </summary>
    public static abstract ISwiftObject NewFromPayload(IntPtr payload);

    /// <summary>
    /// Declares how this type's <see cref="NewFromPayload"/> takes ownership of the wire buffer it is
    /// constructed from — the single declared source of truth the marshal seam reads to balance Swift
    /// ARC and free the temporary correctly (see <see cref="PayloadConstructionSemantics"/>). There is
    /// deliberately no default: every implementer MUST declare its semantics so a new type cannot
    /// silently inherit the wrong cleanup. The seam never invokes this static-virtually from shared
    /// generic code (that triggers the Mono JIT assertion); it is read once via the by-<see cref="Type"/>
    /// <c>PayloadSemanticsDispatcher</c> cache (populated by literal registrations) with a reflection
    /// backstop, exactly mirroring <see cref="GetTypeMetadata"/>.
    /// </summary>
    public static abstract PayloadConstructionSemantics PayloadConstructionSemantics { get; }

    /// <summary>
    /// Marshals this object to a Swift destination
    /// </summary>
    public int MarshalToSwift(ref Span<byte> swiftDestSpan);

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type
    /// </summary>
    public static abstract ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class;

    /// <summary>
    /// Gets the raw Swift handle for interop marshalling.
    /// Types backed by a SafeHandle or existential container override this.
    /// Value-type structs (frozen) do not have meaningful handles and retain the default throw.
    /// </summary>
    IntPtr SwiftHandle => throw new NotSupportedException(
        $"SwiftHandle is not available for {GetType().Name}. Only heap-backed Swift types support handle extraction.");

    /// <summary>
    /// Suppresses the finalizer of this object's backing payload, if it owns one.
    /// Called by <see cref="InteropServices.SwiftMarshal.MarshalCallbackArg{T}"/> for the Adopt
    /// borrow arm (and, via the <see cref="ConsumePayloadBuffer"/> default, for Move-semantics
    /// types with no separable container) when a
    /// borrowed (+0) native reference is wrapped: the wrapper must not release a handle it does
    /// not own, so its payload <see cref="System.Runtime.InteropServices.SafeHandle"/> finalizer
    /// (which would call <c>ReleaseHandle</c> → <c>Arc.Release</c> / VWT destroy) must be suppressed.
    /// This is the non-reflective replacement for the former per-call
    /// <c>GetType().GetProperty("Payload")</c> + boxed <c>GetValue</c> lookup.
    /// The default is a no-op for types with no separately-finalizable payload — value-type
    /// (frozen) structs, existential-container-backed proxies, and SafeHandle-subclass wrappers
    /// whose own finalizer is already suppressed by the caller's <c>GC.SuppressFinalize(this)</c>.
    /// Heap-backed wrappers that hold a payload SafeHandle in a separate field override this to
    /// call <c>GC.SuppressFinalize</c> on that field.
    /// </summary>
    void SuppressPayloadFinalizer() { }

    /// <summary>
    /// Consumes this wrapper's separable payload container for the borrowed (+0) Move marshal
    /// shape. Called by <see cref="InteropServices.SwiftMarshal.MarshalCallbackArg{T}"/> for the
    /// Move arm: a Move-semantics wrapper bitwise-transfers the borrowed value's words into a
    /// container buffer the WRAPPER itself allocated, so it owns the container allocation but not
    /// the value inside it. A type with that two-part shape (e.g. <c>SwiftString</c>) overrides
    /// this to arrange cleanup that frees its own container without value-witness-destroying the
    /// borrowed value — blanket-suppressing the payload finalizer instead would leak the container
    /// on every callback invocation.
    /// The default falls back to the conservative borrowed-wrapper treatment
    /// (suppress the payload finalizer): for a Move-semantics type whose container is NOT
    /// separable from the value, that trades a bounded leak for the alternative — a finalizer
    /// value-witness Destroy over-releasing a value Swift still owns.
    /// </summary>
    void ConsumePayloadBuffer()
    {
        GC.SuppressFinalize(this);
        SuppressPayloadFinalizer();
    }
}

/// <summary>
/// Helper class for Swift invoking ISwiftObject static methods.
/// On NativeAOT (IsDynamicCodeSupported=false), uses direct static virtual dispatch.
/// On Mono JIT (IsDynamicCodeSupported=true), uses reflection to avoid
/// Mono JIT assertion failure (jit-info.c:918, mini-generic-sharing.c:2759).
/// Results are cached so reflection cost is one-time per type.
/// </summary>
public struct SwiftObjectHelper<T> where T : ISwiftObject
{
    /// <summary>
    /// Returns the TypeMetadata for T
    /// </summary>
    /// <returns>the TypeMetadata for T</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetTypeMetadata is always present on ISwiftObject implementations generated by the binding generator")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    public static TypeMetadata GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(T), type =>
        {
            // On NativeAOT, use direct static virtual dispatch.
            // On Mono (JIT or AOT/simulator), use reflection to avoid JIT assertion
            // (jit-info.c:918, mini-generic-sharing.c:2759).
            // The direct dispatch is in a separate method so Mono never compiles it.
            TypeMetadata metadata;
            if (SwiftRuntimeInfo.IsNativeAotRuntime)
            {
                metadata = DirectDispatchGetTypeMetadata();
            }
            else
            {
                // Mono / CoreCLR: resolve cache-first through the typed metadata factory, falling
                // back to the reflective last resort only for unregistered types (Finding 32). We do
                // not register here — the registration lambda's static-abstract T.GetTypeMetadata
                // would assert for a shared generic.
                metadata = SwiftObjectReflectionHelper.ResolveTypeMetadataCacheFirst(type);
            }

            if (!metadata.IsValid)
            {
                throw new InvalidOperationException($"Failed to retrieve type metadata for {type}");
            }

            return metadata;
        });
    }

    /// <summary>
    /// Direct static virtual dispatch — NativeAOT only.
    /// Separate method so Mono JIT never compiles this (avoids jit-info.c:918 assertion).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TypeMetadata DirectDispatchGetTypeMetadata()
    {
        // Register both concrete factories so unconstrained by-Type callers — MarshalFromSwift<T>
        // and the metadata cache-first seam (Finding 32) — resolve without reflection. Registering
        // here too keeps the metadata dispatcher self-healing in lockstep with NewFromPayload on
        // NativeAOT, where T is concrete and these deferred static-abstract lambdas are safe.
        Swift.Runtime.InteropServices.NewFromPayloadDispatcher.Register(
            typeof(T), handle => (object)T.NewFromPayload(handle));
        Swift.Runtime.InteropServices.TypeMetadataDispatcher.Register(
            typeof(T), () => T.GetTypeMetadata());
        return T.GetTypeMetadata();
    }

    /// <summary>
    /// Creates a new Swift object from a given payload
    /// </summary>
    /// <param name="payload"></param>
    /// <returns>a new ISwiftObject</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "NewFromPayload is always present on ISwiftObject implementations generated by the binding generator")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(T) satisfies DynamicallyAccessedMembers at runtime; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    public static ISwiftObject NewFromPayload(IntPtr payload)
    {
        // On NativeAOT, use direct static virtual dispatch.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
            return DirectDispatchNewFromPayload(payload);

        // Mono / CoreCLR: consult the factory cache first (Finding 32). Generated module
        // initializers register a concrete-typed factory for every emitted type on all
        // runtimes, so this is a cache lookup + delegate invoke in the common case rather
        // than per-call reflection. We do not register here on Mono — the registration
        // lambda contains the static-abstract T.NewFromPayload that would assert on Mono.
        var cached = Swift.Runtime.InteropServices.NewFromPayloadDispatcher.TryCreate(typeof(T), payload);
        if (cached != null)
            return (ISwiftObject)cached;
        return SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(T), payload);
    }

    /// <summary>
    /// Direct static virtual dispatch — NativeAOT only.
    /// Separate method so Mono JIT never compiles this (avoids jit-info.c:918 assertion).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISwiftObject DirectDispatchNewFromPayload(IntPtr payload)
    {
        Swift.Runtime.InteropServices.NewFromPayloadDispatcher.Register(
            typeof(T), handle => (object)T.NewFromPayload(handle));
        return T.NewFromPayload(payload);
    }
}

/// <summary>
/// Helper class for invoking ISwiftObject static abstract methods via reflection.
/// This avoids static virtual dispatch in generic contexts which triggers Mono JIT
/// assertion failures (jit-info.c:918, mini-generic-sharing.c:2759).
/// </summary>
internal static class SwiftObjectReflectionHelper
{
    /// <summary>
    /// Resolves a type's Swift metadata cache-first (Finding 32): a type registered through
    /// <see cref="InteropServices.TypeMetadataDispatcher"/> — every generator-emitted type and the
    /// runtime's own concrete ISwiftObject types — resolves via its concrete-typed factory delegate;
    /// only a genuinely-unregistered type (an open Runtime generic whose concrete instantiation cannot
    /// be registered from its shared-generic call site) falls through to the reflective
    /// <see cref="InvokeGetTypeMetadata"/> last resort. This is the single seam the cache-first
    /// metadata lookups share — the Mono/CoreCLR <see cref="SwiftObjectHelper{T}.GetTypeMetadata"/>
    /// branch and the by-Type resolvers (<c>TypeMetadata.TryGetTypeMetadataUncached</c>,
    /// <c>ExistentialContainerFactory.CreateAnyRuntime</c>, which use it on all runtimes) — so the
    /// cache-first contract is expressed and asserted in one place.
    /// </summary>
    internal static TypeMetadata ResolveTypeMetadataCacheFirst(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type)
    {
        if (InteropServices.TypeMetadataDispatcher.TryGet(type, out var metadata))
            return metadata;
        return InvokeGetTypeMetadata(type);
    }

    /// <summary>
    /// Invokes GetTypeMetadata() on the concrete type via reflection, searching for the explicit
    /// interface implementation (ISwiftObject.GetTypeMetadata). This is the reflective <b>last resort</b>
    /// for the metadata lookups (Finding 32): every registered type — all generator-emitted types and
    /// the runtime's own concrete ISwiftObject types — resolves through the typed
    /// <see cref="InteropServices.TypeMetadataDispatcher"/> first, so this name-matched scan runs only
    /// for genuinely-unregistered types (open Runtime generics whose concrete instantiation cannot be
    /// registered from their shared-generic call site). Returns <see cref="TypeMetadata.Zero"/> when no
    /// such member is found, leaving the caller to surface the failure loudly via its IsValid check.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetTypeMetadata is always present on ISwiftObject implementations; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    internal static TypeMetadata InvokeGetTypeMetadata([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType == typeof(TypeMetadata) &&
                method.GetParameters().Length == 0 &&
                method.Name.Contains("GetTypeMetadata"))
            {
                return (TypeMetadata)method.Invoke(null, null)!;
            }
        }
        return TypeMetadata.Zero;
    }

    /// <summary>
    /// Reads the static <c>PayloadConstructionSemantics</c> property on the concrete type via reflection.
    /// The Mono-safe backstop for <see cref="InteropServices.SwiftMarshal.GetPayloadSemanticsForType"/>
    /// when the by-Type cache has no entry; mirrors <see cref="InvokeGetTypeMetadata"/>. Throws (never
    /// returns a guessed value) if the member is absent so a missing registration is loud, not a silent
    /// mis-classified leak/double-free.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "PayloadConstructionSemantics is always present on ISwiftObject implementations (static abstract, no default); types preserved for consumers by the shipped ILLink.Descriptors.xml exactly as GetTypeMetadata/NewFromPayload are")]
    internal static PayloadConstructionSemantics InvokePayloadConstructionSemantics([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType == typeof(PayloadConstructionSemantics) &&
                method.GetParameters().Length == 0 &&
                method.Name.Contains("PayloadConstructionSemantics"))
            {
                return (PayloadConstructionSemantics)method.Invoke(null, null)!;
            }
        }
        throw new InvalidOperationException(
            $"Failed to find PayloadConstructionSemantics on {type}. Every ISwiftObject implementer must declare it; a missing declaration or registration would otherwise mis-classify payload ownership.");
    }

    /// <summary>
    /// Invokes NewFromPayload(IntPtr) on the concrete type via reflection.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "NewFromPayload is always present on ISwiftObject implementations; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    [UnconditionalSuppressMessage("Trimming", "IL2065",
        Justification = "Constructor lookup is a NativeAOT fallback for explicit interface implementations not found via GetMethods")]
    internal static ISwiftObject InvokeNewFromPayload([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type, IntPtr payload)
    {
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType == typeof(ISwiftObject) &&
                method.Name.Contains("NewFromPayload") &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(IntPtr))
            {
                return (ISwiftObject)method.Invoke(null, new object[] { payload })!;
            }
        }

        // Fallback: NativeAOT may not enumerate explicit interface implementations via GetMethods.
        // All ISwiftObject types have a constructor(IntPtr) that NewFromPayload delegates to.
        var ctor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(IntPtr) }, null);
        if (ctor != null)
            return (ISwiftObject)ctor.Invoke(new object[] { payload });

        throw new InvalidOperationException($"Failed to find NewFromPayload on {type}");
    }

    /// <summary>
    /// Invokes GetProtocolConformanceDescriptor&lt;TProtocol&gt;() on the concrete type via reflection.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProtocolConformanceDescriptor is always present on ISwiftObject implementations; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "MakeGenericMethod on GetProtocolConformanceDescriptor which is always present on ISwiftObject implementations")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod is used on methods that exist in all generated bindings")]
    internal static ProtocolConformanceDescriptor InvokeGetProtocolConformanceDescriptor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, Type protocolType)
    {
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType == typeof(ProtocolConformanceDescriptor) &&
                method.Name.Contains("GetProtocolConformanceDescriptor") &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1)
            {
                var closedMethod = method.MakeGenericMethod(protocolType);
                return (ProtocolConformanceDescriptor)closedMethod.Invoke(null, null)!;
            }
        }
        throw new InvalidOperationException(
            $"Failed to find GetProtocolConformanceDescriptor for {type} with protocol {protocolType}");
    }
}

public struct ProtocolConformanceDescriptorHelper<TType, TProtocol>
    where TType : ISwiftObject
    where TProtocol : class
{
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProtocolConformanceDescriptor is always present on ISwiftObject implementations")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "typeof(TType) satisfies DynamicallyAccessedMembers at runtime; types preserved for consumers by the shipped ILLink.Descriptors.xml — the per-binding descriptor delivered in buildTransitive/ for generator-emitted open generics, or Swift.Runtime's own embedded+rooted descriptor for Runtime-owned ISwiftObject types (NOT the BindingTests app's TrimmerRoots.xml, which consumers never receive)")]
    public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor()
    {
        // On NativeAOT, use direct static virtual dispatch.
        // On Mono (JIT or AOT/simulator), use reflection to avoid JIT assertion
        // (jit-info.c:918, mini-generic-sharing.c:2759).
        // The direct dispatch is in a separate method so Mono never compiles it.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
            return DirectDispatch();
        return SwiftObjectReflectionHelper.InvokeGetProtocolConformanceDescriptor(typeof(TType), typeof(TProtocol));
    }

    /// <summary>
    /// Direct static virtual dispatch — NativeAOT only.
    /// Separate method so Mono JIT never compiles this (avoids jit-info.c:918 assertion).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProtocolConformanceDescriptor DirectDispatch()
    {
        // Register conformance factory so unconstrained callers
        // (ProtocolConformanceDescriptor.TryGet) can resolve without reflection.
        Swift.Runtime.InteropServices.ConformanceDispatcher.Register(
            typeof(TType), typeof(TProtocol),
            () => TType.GetProtocolConformanceDescriptor<TProtocol>());
        return TType.GetProtocolConformanceDescriptor<TProtocol>();
    }
}
