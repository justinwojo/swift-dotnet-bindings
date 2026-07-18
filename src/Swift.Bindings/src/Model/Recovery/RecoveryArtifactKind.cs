// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The controlled vocabulary of things the generator emits, at the granularity recovery cares about.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArtifactRole"/> answers "which piece of a declaration is this" — the public member, its
/// P/Invoke, its wrapper. This enum answers the different question "what kind of thing is it, for the
/// purpose of deciding whether it can be withdrawn". The two are not the same axis: a method's public
/// surface, P/Invoke, wrapper, and callback thunks all withdraw together as one bundle, so they share
/// a single kind here while remaining four distinct <see cref="ArtifactRole"/>s.
/// </para>
/// <para>
/// Every member is mapped by <see cref="RecoveryUnitClassifier"/>. Adding one without a rule fails
/// the completeness test and classifies conservatively at runtime, so a new emitter output cannot
/// quietly inherit "droppable".
/// </para>
/// </remarks>
public enum RecoveryArtifactKind
{
    // ── Leaf callables — the whole needs-closure bundle per callable ──────────────────────────

    /// <summary>An instance or static method, with its P/Invoke, wrapper, and callback thunks.</summary>
    Method,

    /// <summary>A constructor or failable factory.</summary>
    Constructor,

    /// <summary>An operator.</summary>
    Operator,

    /// <summary>A module-scope function, not owned by any type.</summary>
    FreeFunction,

    /// <summary>A generated overload that supplies Swift default arguments.</summary>
    DefaultParameterOverload,

    /// <summary>A generated overload that narrows a widened parameter or return type.</summary>
    NarrowingOverload,

    /// <summary>A closure/callback thunk generated for one parameter of one callable.</summary>
    CallbackThunk,

    /// <summary>A helper generated for, and reachable only from, a single callable.</summary>
    ExclusiveHelper,

    // ── Accessors — access surface, never storage ─────────────────────────────────────────────

    /// <summary>A property getter or setter.</summary>
    PropertyAccessor,

    /// <summary>A subscript getter or setter.</summary>
    SubscriptAccessor,

    // ── Representation — the bytes, never withdrawable alone ─────────────────────────────────

    /// <summary>A stored field of a frozen struct, emitted to match Swift's memory layout.</summary>
    StoredFieldCell,

    /// <summary>An enum case payload cell.</summary>
    EnumPayloadCell,

    /// <summary>Anything whose size or alignment feeds a blitted buffer's total size.</summary>
    BufferSizeContributor,

    // ── Type infrastructure ───────────────────────────────────────────────────────────────────

    /// <summary>The emitted C# type declaration itself.</summary>
    TypeShell,

    /// <summary>A type-metadata accessor helper.</summary>
    TypeMetadataAccessor,

    /// <summary>Retain/release, destruction, or disposal support for a type.</summary>
    TypeLifetimeSupport,

    /// <summary>Existential boxing/unboxing support for a type.</summary>
    ExistentialBoxing,

    // ── Protocols: forward view ───────────────────────────────────────────────────────────────

    /// <summary>The generated <c>IFoo</c> interface a consumer calls on a Swift-vended conformer.</summary>
    ForwardInterface,

    /// <summary>One requirement projected onto the forward interface.</summary>
    ForwardInterfaceMember,

    // ── Protocols: reverse conformance (all-or-nothing) ───────────────────────────────────────

    /// <summary>The reverse-dispatch vtable struct, on either side of the boundary.</summary>
    ReverseVtable,

    /// <summary>The generated Swift carrier that wraps a managed implementation.</summary>
    ReverseCarrier,

    /// <summary>One witness filling a reverse-dispatch slot.</summary>
    ReverseWitness,

    /// <summary>The factory that produces a managed conformer for Swift.</summary>
    ManagedConformerFactory,

    /// <summary>A registration that installs a reverse callback so Swift can call into managed code.</summary>
    ReverseCallbackRegistration,

    // ── Conformance edges ─────────────────────────────────────────────────────────────────────

    /// <summary>One generated <c>: IFoo</c> relation on a concrete type.</summary>
    ConformanceDeclaration,

    // ── Shared helpers — declared owners, never an arbitrary nearby member ────────────────────

    /// <summary>UTF-8 slice marshalling helpers.</summary>
    Utf8Helper,

    /// <summary>The generated error registry / error extraction helpers.</summary>
    ErrorRegistry,

    /// <summary>The synthetic EveryProtocol carrier shared by reverse conformances.</summary>
    EveryProtocolCarrier,

    /// <summary>Closure-context allocation and lifetime helpers.</summary>
    ClosureContextHelper,

    /// <summary>NativeAOT type/callback registration emitted for the module.</summary>
    NativeAotRegistration,

    // ── Module ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The module initializer.</summary>
    ModuleInitializer,

    // ── Conservative sink ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An emitted artifact with no recovery rule. Deliberately classified as never-droppable-alone so
    /// an unmodelled output escalates to its owner rather than being withdrawn on an assumption.
    /// A new emitter output should get its own member here, not land in this one.
    /// </summary>
    Unclassified,
}
