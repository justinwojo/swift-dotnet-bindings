// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Actionability tier of a skipped item — the answer to "does a human need to look at this?".
/// Every <see cref="SkipReason"/> maps to exactly one tier via <see cref="SkipDispositionClassifier"/>,
/// so the flat per-item skip list rolls up into "N expected, M to review" without re-deriving each
/// reason's character by hand on every triage pass.
/// </summary>
/// <remarks>
/// Ordered least-actionable → most-actionable so the enum order doubles as display order:
/// nothing-to-do first, "look at this" last.
/// </remarks>
public enum SkipDisposition
{
    /// <summary>
    /// The skipped member is not actually lost: closed CSM projections recover its consumer surface
    /// (e.g. an open-generic <c>items : MusicItemCollection&lt;AnyType&gt;</c> property whose typed
    /// <c>Items()</c> getters are projected per conformer). Least actionable of all — the row exists
    /// only so the base open-generic member is accounted for, and its
    /// <see cref="SkippedItem.RecoveredBy"/> annotation names the recovering projections. Determined
    /// from emission facts, never a name guess.
    /// </summary>
    Recovered,

    /// <summary>
    /// The declaration was never part of the module's public surface, so the consumer never had it
    /// and nothing was lost (module-internal / underscore-internal / on an internal parent type).
    /// </summary>
    ExpectedNonPublic,

    /// <summary>
    /// A correct-by-design skip: the declaration is pruned deliberately, provided by another package,
    /// requires an upstream Swift change to become bindable, or hits a structural limit of the binding
    /// model (e.g. static protocol requirements have no instance witness slot). Defensible; not a
    /// generator bug and not something a triage pass needs to act on.
    /// </summary>
    ExpectedStructural,

    /// <summary>
    /// A documented generator gap where the declaration could in principle be bound but is not yet —
    /// a Swift-wrapper workaround exists. These are the candidates for future generator work, and are
    /// consumer-visible losses, but each has a known cause.
    /// </summary>
    KnownLimitation,

    /// <summary>
    /// The tool cannot yet explain this skip: a missing handler, a stripped wrapper symbol, an
    /// unclassified reason, a protocol dropped for a cause the shape attribution did not identify, or
    /// any future <see cref="SkipReason"/> added without a disposition. This is exactly the set a human
    /// should look at — the "20% to investigate". Review == 0 means every skip has a defensible tier.
    /// </summary>
    Review,
}

/// <summary>
/// Maps each <see cref="SkipReason"/> to a <see cref="SkipDisposition"/>. This is the single table
/// that encodes "is this skip expected or worth a look" so triage never re-derives it per conversation.
/// </summary>
/// <remarks>
/// The map is exhaustive by test, not by compiler: <c>SkipDispositionClassifierTests</c> asserts every
/// <see cref="SkipReason"/> enum value has an explicit entry. A new reason added without one still
/// classifies (defaults to <see cref="SkipDisposition.Review"/>, so it is never silently called
/// "expected") but trips that test — forcing the disposition to be declared here, in one place.
/// </remarks>
public static class SkipDispositionClassifier
{
    private static readonly IReadOnlyDictionary<SkipReason, SkipDisposition> Dispositions =
        new Dictionary<SkipReason, SkipDisposition>
        {
            // ── Expected — never public surface ──────────────────────────────────────────────
            [SkipReason.ModuleInternal] = SkipDisposition.ExpectedNonPublic,
            [SkipReason.UnderscorePrefixInternal] = SkipDisposition.ExpectedNonPublic,
            [SkipReason.ParentModuleInternalNoFallback] = SkipDisposition.ExpectedNonPublic,

            // ── Expected — correct-by-design / structural / provided-elsewhere ────────────────
            [SkipReason.SynthesizedCodable] = SkipDisposition.ExpectedStructural,
            [SkipReason.SwiftUIView] = SkipDisposition.ExpectedStructural,
            [SkipReason.StaticProtocolMember] = SkipDisposition.ExpectedStructural,
            [SkipReason.ExtensionDefault] = SkipDisposition.ExpectedStructural,
            [SkipReason.OwnedByAppleSupplement] = SkipDisposition.ExpectedStructural,
            [SkipReason.AncestorSkipped] = SkipDisposition.ExpectedStructural,
            [SkipReason.Pattern2InternalTypeReach] = SkipDisposition.ExpectedStructural,
            // Retired: kept for total coverage; a consequence of proxy suppression, never "look at this".
            [SkipReason.SuppressedProxyMethodBody] = SkipDisposition.ExpectedStructural,

            // ── Known limitation — documented gap, Swift-wrapper workaround, consumer-visible ─
            [SkipReason.UnsupportedType] = SkipDisposition.KnownLimitation,
            [SkipReason.AnyTypeFallback] = SkipDisposition.KnownLimitation,
            [SkipReason.AsyncProperty] = SkipDisposition.KnownLimitation,
            [SkipReason.SwiftUIConstraint] = SkipDisposition.KnownLimitation,
            [SkipReason.CombineFramework] = SkipDisposition.KnownLimitation,
            [SkipReason.GenericProtocolConstraint] = SkipDisposition.KnownLimitation,
            [SkipReason.UnsatisfiedGenericConstraint] = SkipDisposition.KnownLimitation,
            [SkipReason.UnsupportedSignature] = SkipDisposition.KnownLimitation,
            [SkipReason.UnsupportedExistential] = SkipDisposition.KnownLimitation,
            [SkipReason.UnsupportedClosure] = SkipDisposition.KnownLimitation,
            [SkipReason.UnsupportedAsyncStream] = SkipDisposition.KnownLimitation,
            // Retired (AsyncThrowingStream now bound); classified with its async-stream family.
            [SkipReason.UnsupportedThrowingAsyncStream] = SkipDisposition.KnownLimitation,
            [SkipReason.DuplicateSignature] = SkipDisposition.KnownLimitation,
            [SkipReason.GenericTypeCallback] = SkipDisposition.KnownLimitation,
            [SkipReason.ActorIsolatedAsyncStream] = SkipDisposition.KnownLimitation,
            [SkipReason.ActorIsolatedConstructor] = SkipDisposition.KnownLimitation,
            [SkipReason.NonBlittableCallConvSwift] = SkipDisposition.KnownLimitation,
            [SkipReason.IndeterminatePwtShape] = SkipDisposition.KnownLimitation,
            [SkipReason.IndeterminateStructLayout] = SkipDisposition.KnownLimitation,
            [SkipReason.CovariantReturnNotRepresentable] = SkipDisposition.KnownLimitation,
            [SkipReason.NetUnavailableType] = SkipDisposition.KnownLimitation,
            [SkipReason.AbsentFrameworkType] = SkipDisposition.KnownLimitation,
            // Constrained-extension wrapper (same-name extension collision or narrower-than-parent
            // generic constraints) and generic-enum payload case constructors are decided, documented
            // gaps — conditional-conformance wrapper extensions / open-generic enum case wrappers are
            // not yet supported. Skipped honestly at planning time (no dangling SBW_ claim), so they
            // are attributed KnownLimitation, not the Review-tier MissingWrapperSymbol they replaced.
            [SkipReason.ConstrainedExtensionWrapper] = SkipDisposition.KnownLimitation,
            [SkipReason.GenericEnumCaseConstructor] = SkipDisposition.KnownLimitation,
            // A degraded reverse-dispatch member (throwing PRODUCE stub / consume-only / fail-fast
            // receiver) is a decided, documented capability gap — the {Protocol}Proxy could not be
            // synthesized, so C#-authored conformance to that protocol isn't possible. Consumer-visible
            // but attributed, so KnownLimitation, not Review. The per-site cause lives in Details.
            [SkipReason.SuppressedProxyMemberDegraded] = SkipDisposition.KnownLimitation,

            // ── ObjC binding path ─────────────────────────────────────────────────────────────
            // Dispositions mirror the Swift-side character of each cause: a type the registry
            // simply doesn't carry yet, an unsupported construct, a duplicate-signature collision,
            // and a variadic are consumer-visible documented gaps (KnownLimitation); a
            // deliberately-unavailable API, an accessibility/name-conflict resolution, an empty
            // category, a flattened duplicate selector, and an over-binding with no native symbol
            // are correct-by-design structural skips (ExpectedStructural). None are Review — every
            // ObjC drop the pipeline records has an attributed cause here.
            [SkipReason.ObjCUnresolvableType] = SkipDisposition.KnownLimitation,
            [SkipReason.ObjCUnsupportedConstruct] = SkipDisposition.KnownLimitation,
            [SkipReason.ObjCDuplicateSignature] = SkipDisposition.KnownLimitation,
            [SkipReason.ObjCVariadicFunction] = SkipDisposition.KnownLimitation,
            [SkipReason.ObjCUnavailableApi] = SkipDisposition.ExpectedStructural,
            [SkipReason.ObjCAccessibilityConflict] = SkipDisposition.ExpectedStructural,
            [SkipReason.ObjCEmptyCategory] = SkipDisposition.ExpectedStructural,
            [SkipReason.ObjCDuplicateSelector] = SkipDisposition.ExpectedStructural,
            [SkipReason.ObjCMissingNativeSymbol] = SkipDisposition.ExpectedStructural,

            // ── Review — the tool cannot (yet) explain the skip ───────────────────────────────
            [SkipReason.MissingHandler] = SkipDisposition.Review,
            [SkipReason.MissingWrapperSymbol] = SkipDisposition.Review,
            // EveryProtocol conformance skips are context-dependent: the reason alone cannot tell an
            // internal protocol (expected) from a genuinely-unexplained public one. The per-item
            // Classify(SkippedItem) overload refines this via the recorded shape cause; the reason-only
            // default is Review so an unattributed one always surfaces.
            [SkipReason.EveryProtocolConformanceSkipped] = SkipDisposition.Review,
            [SkipReason.Unknown] = SkipDisposition.Review,
        };

    /// <summary>
    /// The reasons that have an explicit disposition. A <see cref="SkipReason"/> absent from this set
    /// defaults to <see cref="SkipDisposition.Review"/> at runtime and fails the completeness test —
    /// so an unmapped reason is never silently treated as "expected".
    /// </summary>
    public static IReadOnlyCollection<SkipReason> ExplicitlyClassifiedReasons { get; } = Dispositions.Keys.ToHashSet();

    /// <summary>
    /// Classifies a skip reason in isolation. For <see cref="SkipReason.EveryProtocolConformanceSkipped"/>
    /// prefer <see cref="Classify(SkippedItem)"/>, which refines the tier from the recorded shape cause.
    /// </summary>
    public static SkipDisposition Classify(SkipReason reason) =>
        Dispositions.TryGetValue(reason, out var disposition) ? disposition : SkipDisposition.Review;

    /// <summary>
    /// Classifies a single skipped item. Identical to <see cref="Classify(SkipReason)"/> except for
    /// <see cref="SkipReason.EveryProtocolConformanceSkipped"/>, whose actionability depends on the
    /// dropped protocol's shape (recorded into <see cref="SkippedItem.Details"/> by
    /// <see cref="EveryProtocolSkipCause"/>).
    /// </summary>
    public static SkipDisposition Classify(SkippedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // A row the CSM projected a closed typed surface for is not a loss, regardless of the raw skip
        // reason — the annotation is an emission fact, so it overrides the reason-only tier.
        if (item.RecoveredBy is { Count: > 0 })
            return SkipDisposition.Recovered;
        return item.Reason == SkipReason.EveryProtocolConformanceSkipped
            ? EveryProtocolSkipCause.ClassifyDisposition(item.Details)
            : Classify(item.Reason);
    }
}

/// <summary>
/// The controlled vocabulary of causes for an <see cref="SkipReason.EveryProtocolConformanceSkipped"/>
/// skip, shared between the writer (<c>ProtocolHandler</c>, which stamps the cause into the skip
/// <see cref="SkippedItem.Details"/>) and the reader (<see cref="ClassifyDisposition"/>, which buckets
/// it). Keeping the tokens in one place is what lets the classifier read <c>Details</c> without a
/// fragile magic-string contract.
/// </summary>
public static class EveryProtocolSkipCause
{
    /// <summary>The dropped protocol is module-internal — never public surface.</summary>
    public const string ModuleInternal = "module-internal protocol";

    /// <summary>
    /// The dropped protocol has associated types or a <c>Self</c> requirement — a structural limit of
    /// instance-witness reverse dispatch, not an internal-visibility case.
    /// </summary>
    public const string AssociatedTypeOrSelf = "associated-type or Self-constrained protocol";

    /// <summary>
    /// The protocol was dropped before any conformance decision was recorded and its shape did not
    /// identify a specific cause — the genuinely-unexplained case that a human should look at.
    /// </summary>
    public const string NoDecisionRecorded = "no decision recorded";

    // ── Dropped-from-candidacy structural causes ─────────────────────────────────────────
    // A protocol filtered out of `suitableProtocols` (dropped candidacy) BEFORE any
    // RecordConformanceDecision call reaches ProtocolHandler with GetConformanceSkipReason==null
    // and falls back to ForDroppedProtocol, which — for a public, non-Self, non-associated-type
    // protocol — can only report `NoDecisionRecorded` (Review-tier noise). The
    // EmitEveryProtocolConformances post-step attributes each such drop to the specific structural
    // filter that fired (mirroring the suitableProtocols .Where chain in order) and records THIS
    // token, so the skip classifies as ExpectedStructural instead of Review. None of these tokens
    // contain the `NoDecisionRecorded` / `ModuleInternal` substrings, so ClassifyDisposition buckets
    // them structural by exclusion.

    /// <summary>Re-exported stdlib/foreign protocol — its requirements are not defined in this module.</summary>
    public const string DroppedForeignProtocol = "re-exported protocol not defined in this module";

    /// <summary>Class-bound protocol requiring NSObject/AnyObject identity semantics EveryProtocol can't provide.</summary>
    public const string DroppedClassIdentity = "class-bound protocol requiring NSObject/AnyObject identity semantics";

    /// <summary>Protocol requires a concrete class superclass EveryProtocol (a plain class) does not inherit.</summary>
    public const string DroppedClassSuperclass = "protocol requires a concrete class-superclass constraint";

    /// <summary>Inherits a protocol EveryProtocol can't witness (CaseIterable / associated-type / unsatisfied stdlib).</summary>
    public const string DroppedInheritsUnsatisfiable = "inherits a protocol EveryProtocol cannot witness";

    /// <summary>A member signature reaches a module-internal type EveryProtocol can't implement against.</summary>
    public const string DroppedInternalTypeReach = "member signature reaches a module-internal type";

    /// <summary>Sibling protocols require a same-named property at conflicting types — both are dropped.</summary>
    public const string DroppedPropertyTypeConflict = "sibling-protocol property-type conflict";

    /// <summary>A sibling protocol requires a property whose name collides with this protocol's same-named method.</summary>
    public const string DroppedMemberKindConflict = "sibling-protocol member-kind conflict (property vs same-named method)";

    /// <summary>Dropped from EveryProtocol candidacy by a structural filter with no more-specific attribution.</summary>
    public const string DroppedCandidacyStructural = "dropped from EveryProtocol candidacy (structural)";

    /// <summary>
    /// Shape-based cause for a protocol dropped from <c>suitableProtocols</c> before any
    /// <c>RecordConformanceDecision</c> call (so <c>GetConformanceSkipReason</c> returns null).
    /// Internal wins over associated-type/Self so the most-expected tier is reported when both hold.
    /// </summary>
    public static string ForDroppedProtocol(ProtocolDecl protocolDecl)
    {
        ArgumentNullException.ThrowIfNull(protocolDecl);
        if (protocolDecl.IsModuleInternal)
            return ModuleInternal;
        if (protocolDecl.HasSelfRequirement || protocolDecl.AssociatedTypes.Count > 0)
            return AssociatedTypeOrSelf;
        return NoDecisionRecorded;
    }

    /// <summary>
    /// Maps a recorded EveryProtocol skip <see cref="SkippedItem.Details"/> string to a disposition.
    /// Only two cases are special: a module-internal protocol was never public
    /// (<see cref="SkipDisposition.ExpectedNonPublic"/>), and a genuinely-unattributed drop is worth a
    /// look (<see cref="SkipDisposition.Review"/>). Every other cause — the associated-type/Self shape
    /// above, or any specific emit-time decline recorded by <c>EveryProtocolEmitter</c>
    /// (HasSelfRequirement, StaticMethodRequirements, ClassSuperclassRequired, …) — is a defensible
    /// structural limit (<see cref="SkipDisposition.ExpectedStructural"/>).
    /// </summary>
    public static SkipDisposition ClassifyDisposition(string? details)
    {
        if (details == null)
            return SkipDisposition.Review;
        if (details.Contains(ModuleInternal, StringComparison.Ordinal))
            return SkipDisposition.ExpectedNonPublic;
        if (details.Contains(NoDecisionRecorded, StringComparison.Ordinal))
            return SkipDisposition.Review;
        return SkipDisposition.ExpectedStructural;
    }
}
