// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The recovery rule for one <see cref="RecoveryArtifactKind"/>: which unit it belongs to, what it
/// contributes to the binary interface, and whether it can be withdrawn on its own.
/// </summary>
public readonly record struct RecoveryClassification
{
    /// <summary>The scope of the unit this artifact belongs to.</summary>
    public RecoveryScope Scope { get; init; }

    /// <summary>What this artifact contributes to the binary interface.</summary>
    public AbiFootprint Footprint { get; init; }

    /// <summary>
    /// True when this artifact's bytes or index are part of its <em>escalation parent's</em> agreed
    /// layout, so removing it alone would shift a sibling that is still there.
    /// </summary>
    /// <remarks>
    /// This is deliberately not derived from <see cref="Footprint"/>. A reverse-conformance capability
    /// carries <see cref="AbiFootprint.VtableSlot"/> yet withdraws cleanly, because it owns every slot
    /// it counts — the whole vtable leaves at once and nothing retained was measuring it. A frozen
    /// struct's stored field carries <see cref="AbiFootprint.Representation"/> and cannot leave at all
    /// while its type survives, because every later field's offset is stated relative to it. Same
    /// question, opposite answers, so ownership has to be stated rather than inferred.
    /// </remarks>
    public bool ContributesToParentLayout { get; init; }

    /// <summary>
    /// False when the kind has no declared rule and this classification is the conservative fallback.
    /// </summary>
    public bool IsDeclared { get; init; }

    /// <summary>
    /// Whether an artifact of this kind may be withdrawn without first escalating to its owner. A
    /// necessary condition only — the retained set still has to be checked (see
    /// <see cref="RecoveryPolicy.SafeToDrop"/>).
    /// </summary>
    public bool DroppableAlone => !ContributesToParentLayout;
}

/// <summary>
/// Maps every <see cref="RecoveryArtifactKind"/> to its recovery rule. This is the single table that
/// encodes "what can be given up, and at what granularity", so the answer never has to be re-derived
/// per emitter.
/// </summary>
/// <remarks>
/// <para>
/// The map is exhaustive by test, not by compiler: <c>RecoveryUnitClassifierTests</c> asserts every
/// enum value has an explicit entry. A kind added without one still classifies — conservatively, as
/// contributing to its parent's layout, so it escalates instead of being dropped — but trips that
/// test. Adding an emitter output with no recovery rule is therefore loud in exactly the place that
/// matters, and silent nowhere.
/// </para>
/// <para>
/// Vtable slots appear here only as a footprint of the capability that owns them. There is
/// deliberately no kind whose withdrawal means "delete slot N": positional slot layout has one owner,
/// <c>VtableLayout</c>, and recovery only ever removes a member from the forward interface or
/// disables the reverse capability whole. Shrinking the slot list would shift every later field
/// relative to Swift.
/// </para>
/// </remarks>
public static class RecoveryUnitClassifier
{
    private static readonly IReadOnlyDictionary<RecoveryArtifactKind, RecoveryClassification> Rules =
        new Dictionary<RecoveryArtifactKind, RecoveryClassification>
        {
            // ── Leaf callables ────────────────────────────────────────────────────────────────
            // Each owns its own symbol and nothing else's layout is stated in terms of it, which is
            // why per-member skipping has always been sound.
            [RecoveryArtifactKind.Method] = Leaf(AbiFootprint.Symbol),
            [RecoveryArtifactKind.Constructor] = Leaf(AbiFootprint.Symbol),
            [RecoveryArtifactKind.Operator] = Leaf(AbiFootprint.Symbol),
            [RecoveryArtifactKind.FreeFunction] = Leaf(AbiFootprint.Symbol),
            [RecoveryArtifactKind.CallbackThunk] = Leaf(AbiFootprint.Symbol),
            [RecoveryArtifactKind.ExclusiveHelper] = Leaf(AbiFootprint.Symbol),
            // Pure managed convenience over a call that already exists — no native footprint at all.
            [RecoveryArtifactKind.DefaultParameterOverload] = Leaf(AbiFootprint.None),
            [RecoveryArtifactKind.NarrowingOverload] = Leaf(AbiFootprint.None),

            // ── Accessors ─────────────────────────────────────────────────────────────────────
            // Access surface only. Losing the ability to lower an accessor says nothing about the
            // bytes the property stores, so the accessor group leaves and the storage cell stays.
            [RecoveryArtifactKind.PropertyAccessor] = Group(RecoveryScope.AccessorGroup, AbiFootprint.Symbol),
            [RecoveryArtifactKind.SubscriptAccessor] = Group(RecoveryScope.AccessorGroup, AbiFootprint.Symbol),

            // ── Representation ────────────────────────────────────────────────────────────────
            // Never withdrawable alone: every later field's offset, and the type's total size, are
            // stated relative to these. A failure here escalates to the type that owns the layout.
            [RecoveryArtifactKind.StoredFieldCell] = Representation(),
            [RecoveryArtifactKind.EnumPayloadCell] = Representation(),
            [RecoveryArtifactKind.BufferSizeContributor] = Representation(),

            // ── Type infrastructure ───────────────────────────────────────────────────────────
            // A type may survive as an opaque shell when every retained use stays sound, so the type
            // surface itself is withdrawable — it just costs the consumer the whole type.
            [RecoveryArtifactKind.TypeShell] = Group(RecoveryScope.TypeSurface, AbiFootprint.Metadata),
            [RecoveryArtifactKind.TypeMetadataAccessor] = Group(RecoveryScope.TypeSurface, AbiFootprint.Metadata | AbiFootprint.Symbol),
            [RecoveryArtifactKind.TypeLifetimeSupport] = Group(RecoveryScope.TypeSurface, AbiFootprint.Ownership | AbiFootprint.Symbol),
            [RecoveryArtifactKind.ExistentialBoxing] = Group(RecoveryScope.TypeSurface, AbiFootprint.Metadata | AbiFootprint.Symbol),

            // ── Protocols: forward view ───────────────────────────────────────────────────────
            // Dispatch runs through Swift's real witness table, so omitting a requirement leaves the
            // native conformance valid and merely exposes less. Note the absence of VtableSlot: a
            // forward member does not own an index, so dropping it never shrinks a vtable.
            [RecoveryArtifactKind.ForwardInterface] = Group(RecoveryScope.ForwardProtocolView, AbiFootprint.Metadata),
            [RecoveryArtifactKind.ForwardInterfaceMember] = Leaf(AbiFootprint.Symbol),

            // ── Protocols: reverse conformance ────────────────────────────────────────────────
            // All-or-nothing. Every piece maps to the one capability unit, so a single unbindable
            // witness disables the bundle rather than leaving a null or trapping slot behind. The
            // capability owns its slots outright, so withdrawing it whole alters no retained layout.
            [RecoveryArtifactKind.ReverseVtable] = Group(RecoveryScope.ManagedProtocolConformance, AbiFootprint.VtableSlot | AbiFootprint.Representation),
            [RecoveryArtifactKind.ReverseCarrier] = Group(RecoveryScope.ManagedProtocolConformance, AbiFootprint.Metadata | AbiFootprint.Symbol),
            [RecoveryArtifactKind.ReverseWitness] = Group(RecoveryScope.ManagedProtocolConformance, AbiFootprint.VtableSlot | AbiFootprint.Symbol),
            [RecoveryArtifactKind.ManagedConformerFactory] = Group(RecoveryScope.ManagedProtocolConformance, AbiFootprint.Symbol),
            [RecoveryArtifactKind.ReverseCallbackRegistration] = Group(RecoveryScope.ManagedProtocolConformance, AbiFootprint.Symbol),

            // ── Conformance edges ─────────────────────────────────────────────────────────────
            [RecoveryArtifactKind.ConformanceDeclaration] = Group(RecoveryScope.ConformanceEdge, AbiFootprint.Metadata),

            // ── Shared helpers ────────────────────────────────────────────────────────────────
            // Withdrawable only with their full owner closure; the graph's Requires edges are what
            // enforce that, so the classification itself stays permissive.
            [RecoveryArtifactKind.Utf8Helper] = Group(RecoveryScope.SharedHelperBundle, AbiFootprint.Symbol),
            [RecoveryArtifactKind.ErrorRegistry] = Group(RecoveryScope.SharedHelperBundle, AbiFootprint.Symbol),
            [RecoveryArtifactKind.EveryProtocolCarrier] = Group(RecoveryScope.SharedHelperBundle, AbiFootprint.Metadata | AbiFootprint.Symbol),
            [RecoveryArtifactKind.ClosureContextHelper] = Group(RecoveryScope.SharedHelperBundle, AbiFootprint.Symbol),
            [RecoveryArtifactKind.NativeAotRegistration] = Group(RecoveryScope.SharedHelperBundle, AbiFootprint.Symbol),

            // ── Module ────────────────────────────────────────────────────────────────────────
            [RecoveryArtifactKind.ModuleInitializer] = Group(RecoveryScope.Module, AbiFootprint.Symbol),

            // ── Conservative sink ─────────────────────────────────────────────────────────────
            // Smallest attributable scope so attribution stays honest, but never droppable alone, so
            // an unmodelled artifact escalates to its owner instead of being given up on a guess.
            [RecoveryArtifactKind.Unclassified] = new RecoveryClassification
            {
                Scope = RecoveryScope.LeafApi,
                Footprint = AbiFootprint.Unknown,
                ContributesToParentLayout = true,
                IsDeclared = true,
            },
        };

    /// <summary>
    /// The kinds that have an explicit rule. A <see cref="RecoveryArtifactKind"/> absent from this set
    /// classifies conservatively at runtime and fails the completeness test.
    /// </summary>
    public static IReadOnlyCollection<RecoveryArtifactKind> ExplicitlyClassifiedKinds { get; } =
        Rules.Keys.ToHashSet();

    /// <summary>
    /// Classifies an artifact kind. An unmapped kind returns the conservative fallback —
    /// smallest attributable scope, unknown footprint, never droppable alone — so the caller
    /// escalates rather than assuming the artifact is safe to withdraw.
    /// </summary>
    public static RecoveryClassification Classify(RecoveryArtifactKind kind) =>
        Rules.TryGetValue(kind, out var rule)
            ? rule
            : new RecoveryClassification
            {
                Scope = RecoveryScope.LeafApi,
                Footprint = AbiFootprint.Unknown,
                ContributesToParentLayout = true,
                IsDeclared = false,
            };

    /// <summary>The unit scope an artifact of this kind belongs to.</summary>
    public static RecoveryScope ScopeOf(RecoveryArtifactKind kind) => Classify(kind).Scope;

    /// <summary>
    /// Maps one of session 04's emitted artifacts onto its recovery kind, given the declaration it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// The public C# surface, the P/Invoke behind it, the Swift wrapper, and every callback thunk of
    /// one callable all answer the same "can this be withdrawn" question, so they collapse to one
    /// kind and therefore one unit — the needs-closure bundle the design calls for. The roles that
    /// fan <em>in</em> (a metadata helper shared by many members, a reverse vtable shared by a whole
    /// protocol) map to their owning scope instead.
    /// Returns false, with <paramref name="kind"/> set to <see cref="RecoveryArtifactKind.Unclassified"/>,
    /// for a role/declaration pairing the generator does not produce.
    /// </remarks>
    public static bool TryFromArtifact(ArtifactRole role, BindingItemKind declKind, out RecoveryArtifactKind kind)
    {
        kind = RecoveryArtifactKind.Unclassified;
        switch (role)
        {
            case ArtifactRole.CSharpPublic:
            case ArtifactRole.PInvoke:
            case ArtifactRole.SwiftWrapper:
            case ArtifactRole.Callback:
                switch (declKind)
                {
                    case BindingItemKind.Method: kind = RecoveryArtifactKind.Method; return true;
                    case BindingItemKind.Operator: kind = RecoveryArtifactKind.Operator; return true;
                    case BindingItemKind.Property: kind = RecoveryArtifactKind.PropertyAccessor; return true;
                    case BindingItemKind.Subscript: kind = RecoveryArtifactKind.SubscriptAccessor; return true;
                    case BindingItemKind.Type: kind = RecoveryArtifactKind.TypeShell; return true;
                    // A module-scoped callable is a free function; the module's own C# surface is
                    // its initializer, which arrives under its own role below.
                    case BindingItemKind.Module: kind = RecoveryArtifactKind.FreeFunction; return true;
                    default: return false;
                }

            case ArtifactRole.MetadataHelper:
                kind = RecoveryArtifactKind.TypeMetadataAccessor;
                return true;

            case ArtifactRole.ReverseVtable:
                kind = RecoveryArtifactKind.ReverseVtable;
                return true;

            case ArtifactRole.ModuleInitializer:
                kind = RecoveryArtifactKind.ModuleInitializer;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// <see cref="TryFromArtifact"/>, returning <see cref="RecoveryArtifactKind.Unclassified"/> for a
    /// pairing the generator does not produce — which classifies as escalate-to-parent rather than
    /// as droppable.
    /// </summary>
    public static RecoveryArtifactKind FromArtifact(ArtifactRole role, BindingItemKind declKind) =>
        TryFromArtifact(role, declKind, out var kind) ? kind : RecoveryArtifactKind.Unclassified;

    private static RecoveryClassification Leaf(AbiFootprint footprint) => new()
    {
        Scope = RecoveryScope.LeafApi,
        Footprint = footprint,
        ContributesToParentLayout = false,
        IsDeclared = true,
    };

    private static RecoveryClassification Group(RecoveryScope scope, AbiFootprint footprint) => new()
    {
        Scope = scope,
        Footprint = footprint,
        ContributesToParentLayout = false,
        IsDeclared = true,
    };

    private static RecoveryClassification Representation() => new()
    {
        Scope = RecoveryScope.TypeRepresentation,
        Footprint = AbiFootprint.Representation,
        ContributesToParentLayout = true,
        IsDeclared = true,
    };
}
