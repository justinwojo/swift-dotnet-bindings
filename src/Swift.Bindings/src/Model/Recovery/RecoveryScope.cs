// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The granularity at which a generated surface can be withdrawn soundly.
/// </summary>
/// <remarks>
/// <para>
/// A method is an emission convenience, not a soundness boundary: removing one changes nothing
/// about how anything else is laid out, but removing a stored field shifts every field after it.
/// So recovery granularity has to equal <em>layout/capability ownership</em> granularity, and this
/// enum is that ownership vocabulary. Each member answers "if this fails, what is the smallest
/// thing I can withdraw without lying about the ABI?".
/// </para>
/// <para>
/// The scopes are ordered by <see cref="RecoveryScopeLattice.Rank"/>: escalation normally moves to a
/// strictly coarser rank and terminates at <see cref="Module"/>. The one exception is a type
/// escalating into its <em>containing</em> type, which is same-scope; see
/// <see cref="RecoveryScopeLattice.CanEscalateTo"/> for why that still cannot cycle. Declaration
/// order is least-severe first, so the enum order doubles as display order.
/// </para>
/// </remarks>
public enum RecoveryScope
{
    /// <summary>
    /// One callable and everything generated exclusively for it — the public C# member, its
    /// P/Invoke(s), the Swift wrapper, callback thunks, default-parameter and narrowing overloads,
    /// and its exclusive helpers. Methods, constructors, operators, free functions. Withdrawing the
    /// whole bundle changes no other surface's ABI, which is why the existing per-member skip
    /// machinery is already sound.
    /// </summary>
    LeafApi,

    /// <summary>
    /// The getter and setter of one property or subscript, taken together. Separate from
    /// <see cref="TypeRepresentation"/> on purpose: a stored property has two roles, and losing the
    /// ability to lower its accessors says nothing about the bytes it contributes. The accessors may
    /// go; the storage cell may not.
    /// </summary>
    AccessorGroup,

    /// <summary>
    /// The C#-visible subset of a protocol that a consumer calls on a Swift value which
    /// <em>already</em> conforms. Dispatch goes through Swift's real witness table, so omitting a
    /// requirement the generator cannot bind is safe — the native conformance stays valid and C#
    /// simply exposes less.
    /// </summary>
    ForwardProtocolView,

    /// <summary>
    /// The capability of wrapping a C# implementation in a generated Swift carrier so Swift can call
    /// back into managed code — carrier, vtable setter, conformer factory, witness getter, reverse
    /// callback registrations. Swift conformance is all-or-nothing: one unbindable witness disables
    /// the entire bundle. A null or trapping witness is never an option, because a deliberate trap
    /// still crashes at runtime.
    /// </summary>
    ManagedProtocolConformance,

    /// <summary>
    /// One generated <c>: IFoo</c> relation on a concrete type. Removing the edge is bounded, but it
    /// propagates to every retained API whose signature depends on the conformance.
    /// </summary>
    ConformanceEdge,

    /// <summary>
    /// A helper several surfaces share — UTF-8 slice helpers, error registries, EveryProtocol
    /// carriers, closure-context helpers, NativeAOT registration. A shared helper never picks an
    /// arbitrary nearby member to blame: it declares its owners, and goes only with all of them.
    /// </summary>
    SharedHelperBundle,

    /// <summary>
    /// The memory layout of a type — frozen-struct stored fields, enum payloads, buffer size and
    /// alignment, by-value register classification. <b>Never withdrawable in isolation.</b> Guessing
    /// or eliding any part of it is the canonical compile-clean/ABI-corrupt outcome, so a failure
    /// here escalates to the type that owns the layout.
    /// </summary>
    TypeRepresentation,

    /// <summary>
    /// A whole type and its infrastructure — metadata access, retain/release, boxing, factories.
    /// The escalation terminus for types: a type may survive as an opaque shell when every retained
    /// use of it stays sound, otherwise it goes entirely.
    /// </summary>
    TypeSurface,

    /// <summary>
    /// The whole binding. The floor of last resort, not a default: a localized failure that reaches
    /// here means no coarser-but-still-sound withdrawal existed.
    /// </summary>
    Module,
}

/// <summary>
/// The escalation ordering over <see cref="RecoveryScope"/>: which scopes are coarser than which,
/// and where a scope escalates by default.
/// </summary>
/// <remarks>
/// <para>
/// Escalation is defined by an ordering rather than by a hand-written parent chain, so a recovery
/// walk provably terminates. Individual units still name their own escalation parent — a free
/// function escalates straight to the module, a type member to its type — and this type only
/// constrains which parents are legal.
/// </para>
/// <para>
/// The ordering is <em>not</em> rank alone. A nested type escalates into its containing type, and
/// both are <see cref="RecoveryScope.TypeSurface"/>; a rank-strict rule would outlaw the one
/// escalation the generator already performs (the parent-type-skipped cascade) and force a nested
/// type to blame the whole module instead. So the well-founded measure is the pair
/// <c>(Rank, nesting depth)</c>: each step either strictly increases the rank, or holds the rank and
/// strictly decreases declaration nesting depth. Rank is bounded above and depth below, so no chain
/// is infinite.
/// </para>
/// </remarks>
public static class RecoveryScopeLattice
{
    /// <summary>
    /// Coarseness of a scope. Higher means "withdrawing this costs the consumer more". Only the
    /// ordering is meaningful; the absolute values are not a public contract.
    /// </summary>
    public static int Rank(RecoveryScope scope) => scope switch
    {
        RecoveryScope.LeafApi => 0,
        RecoveryScope.AccessorGroup => 0,
        RecoveryScope.ForwardProtocolView => 1,
        RecoveryScope.ManagedProtocolConformance => 1,
        RecoveryScope.ConformanceEdge => 1,
        RecoveryScope.SharedHelperBundle => 1,
        RecoveryScope.TypeRepresentation => 2,
        RecoveryScope.TypeSurface => 3,
        RecoveryScope.Module => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled recovery scope."),
    };

    /// <summary>The unique coarsest scope — every escalation chain ends here.</summary>
    public static RecoveryScope Terminus => RecoveryScope.Module;

    /// <summary>
    /// Whether <paramref name="parent"/> is a legal escalation target scope for
    /// <paramref name="child"/> — strictly coarser, or the same scope where same-scope nesting is
    /// meaningful (see <see cref="PermitsSameScopeNesting"/>). Never back down.
    /// </summary>
    /// <remarks>
    /// A same-scope answer here is a <em>necessary</em>, not sufficient, condition:
    /// <see cref="RecoveryGraphBuilder"/> additionally requires the parent declaration to enclose the
    /// child's, which is what makes the depth half of the measure decrease.
    /// </remarks>
    public static bool CanEscalateTo(RecoveryScope child, RecoveryScope parent) =>
        Rank(parent) > Rank(child) || (parent == child && PermitsSameScopeNesting(child));

    /// <summary>
    /// Whether a scope can escalate into another unit at the <em>same</em> scope. True only for
    /// <see cref="RecoveryScope.TypeSurface"/>: Swift types nest, so a nested type's coarsest
    /// meaningful blame is its containing type, not the module. Every other scope escalates upward
    /// by rank.
    /// </summary>
    public static bool PermitsSameScopeNesting(RecoveryScope scope) =>
        scope == RecoveryScope.TypeSurface;

    /// <summary>Stable kebab-case wire token, for canonical ids and persisted reports.</summary>
    public static string ToToken(RecoveryScope scope) => scope switch
    {
        RecoveryScope.LeafApi => "leaf-api",
        RecoveryScope.AccessorGroup => "accessor-group",
        RecoveryScope.ForwardProtocolView => "forward-protocol-view",
        RecoveryScope.ManagedProtocolConformance => "managed-protocol-conformance",
        RecoveryScope.ConformanceEdge => "conformance-edge",
        RecoveryScope.SharedHelperBundle => "shared-helper-bundle",
        RecoveryScope.TypeRepresentation => "type-representation",
        RecoveryScope.TypeSurface => "type-surface",
        RecoveryScope.Module => "module",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled recovery scope."),
    };

    /// <summary>Inverse of <see cref="ToToken"/>.</summary>
    public static bool TryParseToken(string? token, out RecoveryScope scope)
    {
        switch (token)
        {
            case "leaf-api": scope = RecoveryScope.LeafApi; return true;
            case "accessor-group": scope = RecoveryScope.AccessorGroup; return true;
            case "forward-protocol-view": scope = RecoveryScope.ForwardProtocolView; return true;
            case "managed-protocol-conformance": scope = RecoveryScope.ManagedProtocolConformance; return true;
            case "conformance-edge": scope = RecoveryScope.ConformanceEdge; return true;
            case "shared-helper-bundle": scope = RecoveryScope.SharedHelperBundle; return true;
            case "type-representation": scope = RecoveryScope.TypeRepresentation; return true;
            case "type-surface": scope = RecoveryScope.TypeSurface; return true;
            case "module": scope = RecoveryScope.Module; return true;
            default: scope = default; return false;
        }
    }
}
