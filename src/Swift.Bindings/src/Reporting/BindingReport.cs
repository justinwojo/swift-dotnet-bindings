// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Binding generation coverage and skip report.
/// </summary>
public sealed class BindingReport
{
    public required string ModuleName { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TotalTypes { get; set; }
    public int EmittedTypes { get; set; }
    public int SkippedTypes { get; set; }

    public int TotalMembers { get; set; }
    public int EmittedMembers { get; set; }
    public int SkippedMembers { get; set; }
    public int SynthesizedMembers { get; set; }

    /// <summary>
    /// Per-kind breakdown of emitted members (methods, properties, operators, subscripts).
    /// </summary>
    public Dictionary<BindingItemKind, int> EmittedMembersByKind { get; } = new();

    /// <summary>
    /// Per-kind breakdown of skipped members.
    /// </summary>
    public Dictionary<BindingItemKind, int> SkippedMembersByKind { get; } = new();

    public List<SkippedItem> SkippedItems { get; } = new();
    public List<WrappedItem> WrappedItems { get; } = new();
    public List<BridgedViewItem> BridgedViews { get; } = new();
    public List<ThemeBridgedItem> ThemeBridgedProperties { get; } = new();

    /// <summary>
    /// Every overload-disambiguation decision this run made — see <see cref="OverloadRenameItem"/>.
    /// Written to <c>binding-report.json</c> so the ship gate can assert the invariant against the
    /// resolver's decisions instead of guessing at emitted identifiers.
    /// </summary>
    public List<OverloadRenameItem> OverloadRenames { get; } = new();

    /// <summary>
    /// Distinct <c>// Unsupported:</c> comment-drops emitted this run (Finding 53) — a type or
    /// member the generator could not bind and left as a comment in the generated C#. Each is
    /// surfaced as a loud <c>SWIFTBIND025</c> diagnostic at report time so a dropped declaration is
    /// never silent. Populated by <see cref="ReportCollector"/> from the ambient comment chokepoint.
    /// </summary>
    public List<string> UnsupportedCommentDrops { get; } = new();

    /// <summary>
    /// The same drops as <see cref="UnsupportedCommentDrops"/>, in the same order, each paired with
    /// the canonical <see cref="BindingsGeneration.DeclId"/> of the declaration it replaced. The
    /// plain string list stays for consumers that already read it; this one is what a tool keys on
    /// to correlate a drop with the declaration across regenerations.
    /// </summary>
    public List<UnsupportedCommentDropItem> UnsupportedCommentDropDetails { get; } = new();

    /// <summary>
    /// Distinct Swift types that degraded to bare <c>object</c> with no <c>[UnsupportedSwiftType]</c>
    /// marker (Finding 53) — e.g. an existential the resolver could not project at a closure
    /// parameter/return position. Each is surfaced as a loud <c>SWIFTBIND026</c> diagnostic so the
    /// otherwise-silent collapse to an untyped value is observable.
    /// </summary>
    public List<string> ObjectDegradations { get; } = new();

    /// <summary>
    /// Distinct Apple-framework reference types bridged to their ObjC class purely by the
    /// naming-convention heuristic (F10 Stage 20) — recognized as an ObjC class by owning module +
    /// class-name prefix alone, with no database record. Unlike the two lists above this is a
    /// SUCCESSFUL bridge, not a degradation, so it carries no loud diagnostic; it is recorded here
    /// (and round-tripped into <c>binding-report.json</c>) so the heuristic guess is observable.
    /// Populated by <see cref="ReportCollector"/> from <c>TypeProjectionFactory</c>'s ObjC fallbacks.
    /// </summary>
    public List<string> ObjCPrefixBridges { get; } = new();

    /// <summary>
    /// How many emitted types are "orphan shells": named by the signature of at least one
    /// closure-tombstoned member (the SB0005 shape — the member exists but every call throws) and
    /// carrying no callable member of their own. A type in this set is reachable in the binding only
    /// as the parameter or return type of something a consumer cannot actually call, so it is surface
    /// that costs a name and delivers nothing.
    /// </summary>
    /// <remarks>
    /// This is an approximation of "emitted only because a tombstoned member referenced it", and
    /// deliberately so: emission records no type-to-type provenance, so nothing can prove a type
    /// would have been dropped had the tombstone not existed. What IS provable is the pair of facts
    /// above — referenced-by-a-tombstone and zero-callable-surface — and their intersection is the
    /// population the widening decision cares about. It over-counts a type that is genuinely useful
    /// with no members of its own (a marker/tag type) and under-counts a type stranded by a member
    /// that was skipped outright rather than tombstoned, since a skip records no signature.
    /// <para>
    /// Computed once emission has settled, and therefore BEFORE the wrapper-strip co-gating that
    /// <c>BindingReportProjection</c> applies when rebuilding the report from the artifact manifest.
    /// A member whose wrapper symbol is stripped after the fact still counts as callable surface
    /// here, so a type stranded by that late removal is not in the set. The metric is deliberately
    /// pre-co-gating: recomputing it there would need the per-session reference data the manifest
    /// does not carry, and an approximation with a stated boundary beats one with a hidden one.
    /// </para>
    /// <para>
    /// The resolved set rides the manifest (<c>GenerationSection.ClosureOrphanShellTypes</c>) and is
    /// restored by the projection, so the number in a written <c>binding-report.json</c> is the one
    /// the live session computed rather than a zero left behind by the rederivation.
    /// </para>
    /// </remarks>
    public int ClosureOrphanShellTypeCount { get; set; }

    /// <summary>
    /// The module-qualified names behind <see cref="ClosureOrphanShellTypeCount"/>, sorted, so the
    /// count is auditable rather than a bare number to be taken on faith.
    /// </summary>
    public List<string> ClosureOrphanShellTypes { get; } = new();

    /// <summary>
    /// Summary of SwiftUI bridge coverage for this module.
    /// Null when the module has no SwiftUI views.
    /// </summary>
    public BridgeSummary? BridgeSummary { get; set; }

    /// <summary>
    /// Actionability roll-up of <see cref="SkippedItems"/> — how many skips are expected vs. worth a
    /// look, plus the short "to review" list. Computed as a pure function of the settled skip list at
    /// projection time (see <see cref="BindingReportProjection.Project"/>). Null only on a report that
    /// was never projected (e.g. a mid-pipeline accumulator).
    /// </summary>
    public SkipTriageSummary? SkipTriage { get; set; }

    /// <summary>
    /// Every emitted member that carries a safety <c>[Obsolete]</c> marker (SB0001 / SB0002 /
    /// SB0009), with the wrapper-eligibility guard that caused it. These members are NOT skips —
    /// they are on the public surface and a consumer can call them — so they never appear in
    /// <see cref="SkippedItems"/>. Ordered by descending prominence, then by name.
    /// </summary>
    public List<DegradedMemberItem> DegradedMembers { get; } = new();

    /// <summary>
    /// Roll-up of <see cref="DegradedMembers"/>: how many, by which diagnostic, by which guard,
    /// and the short "these are the load-bearing ones" list. A count of markers is not a measure
    /// of severity — a marker on a leaf convenience overload is a footnote, the same marker on a
    /// library's only entry point makes the binding look broken.
    /// </summary>
    public DegradedSurfaceSummary? DegradedSurface { get; set; }

    /// <summary>
    /// Whether this module needed a Swift wrapper at all, and how much of its surface depends on
    /// one. Ground truth for the SDK's <c>SwiftWrapperRequired</c> opt-out: a module whose wrapper
    /// artifacts export nothing loses nothing when a wrapper cannot be built, while one that exports
    /// hundreds of entry points silently loses that surface. Zero wrapped MEMBERS is not the same
    /// question — see <see cref="WrapperRequirementSummary.WrapperRequired"/>.
    /// </summary>
    public WrapperRequirementSummary? WrapperRequirement { get; set; }
}

/// <summary>
/// One emitted-but-safety-marked member. The marker text a consumer reads is built from the same
/// <see cref="WrapperReasonDescription"/> recorded here, so the report and the binding agree.
/// </summary>
public sealed class DegradedMemberItem
{
    /// <summary>Declaration kind of the marked member.</summary>
    public required BindingItemKind Kind { get; init; }

    /// <summary>Emitted C# member name.</summary>
    public required string Name { get; init; }

    /// <summary>Module-qualified declaring type, or null for a free function.</summary>
    public string? ContainingType { get; init; }

    /// <summary>The marker actually written onto the member: <c>SB0001</c>, <c>SB0002</c> or <c>SB0009</c>.</summary>
    public required string DiagnosticId { get; init; }

    /// <summary>
    /// The raw wrapper-eligibility guard token (for example <c>closure_params</c>), or null when
    /// the marker has no wrapper cause — a missing exported symbol carries SB0002 on its own.
    /// </summary>
    public string? WrapperReason { get; init; }

    /// <summary>Consumer-facing sentence for <see cref="WrapperReason"/>.</summary>
    public string? WrapperReasonDescription { get; init; }

    /// <summary>True when the member is also deprecated by the library itself.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>True when the member is static (reachable without an instance).</summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Prominence rank; higher means the marker is more likely to sit on a load-bearing entry
    /// point. Report-only and heuristic — see <see cref="ProminenceFactors"/> for what it is made of.
    /// </summary>
    public int ProminenceScore { get; set; }

    /// <summary>The named signals that produced <see cref="ProminenceScore"/>, so the number is auditable.</summary>
    public List<string> ProminenceFactors { get; } = new();
}

/// <summary>Roll-up of the safety-marked surface for one module.</summary>
public sealed class DegradedSurfaceSummary
{
    /// <summary>Total members carrying a safety marker.</summary>
    public int Total { get; set; }

    /// <summary>Marker counts keyed by diagnostic id.</summary>
    public Dictionary<string, int> ByDiagnosticId { get; } = new(StringComparer.Ordinal);

    /// <summary>Marker counts keyed by wrapper-eligibility guard token.</summary>
    public Dictionary<string, int> ByWrapperReason { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The highest-prominence marked members, most prominent first. A subset of
    /// <see cref="BindingReport.DegradedMembers"/> — the full list is always present, so this
    /// list ranks rather than truncates.
    /// </summary>
    public List<DegradedMemberItem> TopDegradedMembers { get; } = new();
}

/// <summary>Whether a module's surface depends on a generated Swift wrapper.</summary>
public sealed class WrapperRequirementSummary
{
    /// <summary>
    /// True when the emitted wrapper artifacts export something the generated C# binds to — an
    /// <c>@_cdecl</c>/<c>@_silgen_name</c> entry point in the wrapper source, or a native thunk
    /// assembly. Not the same as <see cref="WrappedMemberCount"/> being non-zero: helper, metadata
    /// and reverse-dispatch symbols are exported without belonging to any one member.
    /// </summary>
    public bool WrapperRequired { get; set; }

    /// <summary>
    /// How many <c>@_cdecl</c>/<c>@_silgen_name</c> entry points the emitted wrapper source declares
    /// — the complete measure of how much a missing wrapper costs, and the number the rationale is
    /// phrased from.
    ///
    /// <para>Counted from the wrapper source as emitted, before the wrapper compile runs; a symbol
    /// that compile later strips is still counted here. Co-gating does not revise it, because one
    /// stripped member does not correspond to one entry point — helper and metadata symbols are
    /// shared across members, and a single member can export several.</para>
    /// </summary>
    public int WrapperEntryPointCount { get; set; }

    /// <summary>
    /// How many members the report records as wrapped through a bypass, bridge or tombstone path.
    ///
    /// <para>A member routed through an ordinary <c>@_cdecl</c> method wrapper is not recorded
    /// per-member, so this is a floor on wrapper-dependent members rather than a total — read
    /// <see cref="WrapperEntryPointCount"/> for the complete measure.</para>
    /// </summary>
    public int WrappedMemberCount { get; set; }

    /// <summary>How many members carry a safety marker because no wrapper was available.</summary>
    public int UnwrappedMarkedMemberCount { get; set; }

    /// <summary>
    /// One sentence stating what building this binding without a wrapper would cost, phrased for
    /// whoever is deciding whether <c>SwiftWrapperRequired=false</c> is honest for this library.
    /// </summary>
    public string? Rationale { get; set; }
}

/// <summary>
/// Category of declaration being tracked in the report.
/// </summary>
public enum BindingItemKind
{
    Type,
    Method,
    Property,
    Operator,
    Subscript,

    /// <summary>
    /// A whole Swift module. Never used for a skip row — modules fail, they aren't skipped —
    /// but <see cref="DeclId"/> needs a kind for module-scoped artifacts (the module
    /// initializer, module-level helpers), and a second parallel kind taxonomy would be one
    /// more thing to keep in sync. Appended last so existing ordinals are unchanged.
    /// </summary>
    Module,
}

/// <summary>
/// Reason why an item was skipped.
/// </summary>
public enum SkipReason
{
    UnsupportedType,
    AnyTypeFallback,
    AsyncProperty,
    SwiftUIConstraint,
    CombineFramework,
    GenericProtocolConstraint,
    UnsatisfiedGenericConstraint,
    UnsupportedSignature,
    UnsupportedExistential,
    UnsupportedClosure,
    UnsupportedAsyncStream,
    /// <summary>
    /// <b>Retired.</b> <c>AsyncThrowingStream</c> is now bound: it projects to
    /// <c>IAsyncEnumerable&lt;T&gt;</c> like <c>AsyncStream</c>, and a <c>finish(throwing:)</c>
    /// termination is marshalled through a producer-error callback that faults the channel so the
    /// consumer's <c>await foreach</c> rethrows. The generator no longer emits this reason for the
    /// throwing variant. The member is retained so persisted reports and the
    /// <see cref="WorkaroundRecommendations"/> switch keep total coverage without churn.
    /// </summary>
    UnsupportedThrowingAsyncStream,
    DuplicateSignature,
    MissingHandler,
    SwiftUIView,
    StaticProtocolMember,
    GenericTypeCallback,
    ActorIsolatedAsyncStream,
    SynthesizedCodable,
    UnderscorePrefixInternal,
    ModuleInternal,
    ExtensionDefault,
    NonBlittableCallConvSwift,
    EveryProtocolConformanceSkipped,
    OwnedByAppleSupplement,
    IndeterminatePwtShape,
    /// <summary>
    /// Frozen value-with-memory struct (projected as a blitted-Buffer class) whose Buffer layout
    /// cannot be sized cross-compile because a stored field is a generic value-type instantiation
    /// (e.g. <c>ClosedRange&lt;Int&gt;</c>, <c>Result&lt;T,E&gt;</c>) — its inline size depends on
    /// the type arguments, which the bare TypeDatabase record does not carry, and the iOS/device
    /// slice exposes no live metadata. Emitting a guessed Buffer would mis-size the field and
    /// corrupt the heap, so the type fails closed.
    /// </summary>
    IndeterminateStructLayout,
    AncestorSkipped,
    ActorIsolatedConstructor,
    MissingWrapperSymbol,
    /// <summary>
    /// A method on a generic parent whose constrained-extension shape cannot be emitted as an
    /// unconditional conformance wrapper: either an unconstrained extension method collides with a
    /// same-name overload on the parent (the wrapper cannot disambiguate), or the method carries
    /// generic constraints narrower than its parent declares (the wrapper extension is emitted
    /// without a where-clause, so the constrained method is invisible at the call site). Decided at
    /// planning time so no <c>@_cdecl</c> wrapper symbol is ever claimed — this replaces the
    /// mis-classified <see cref="MissingWrapperSymbol"/> rollback the two arms formerly produced.
    /// Conditional-conformance wrapper extensions are not yet supported (session 07 territory).
    /// </summary>
    ConstrainedExtensionWrapper,
    /// <summary>
    /// A generic enum's payload-carrying case constructor. The construction wrapper would need a
    /// per-instantiation <c>@_cdecl</c> that the generator does not emit for open-generic enum
    /// cases, so the case constructor is skipped honestly at planning time rather than claiming a
    /// wrapper symbol. Truthful successor to a former <see cref="MissingWrapperSymbol"/> label on
    /// this path.
    /// </summary>
    GenericEnumCaseConstructor,
    /// <summary>
    /// <b>Retired.</b> Once produced by the generate-then-strip proxy co-gater to mark a method
    /// body removed because it constructed a suppressed <c>{Name}Proxy</c>. Proxy suppression is
    /// now decided at emission (the reference gate drops the wrap lambda or stubs the member in
    /// place), so the generator no longer emits this reason. The member is retained so the
    /// <see cref="WorkaroundRecommendations"/> switch keeps total coverage without churn.
    /// </summary>
    SuppressedProxyMethodBody,
    CovariantReturnNotRepresentable,
    /// <summary>
    /// Member's signature reaches a type listed in <c>ModuleDecl.InternalTypeNames</c>
    /// (e.g. <c>@usableFromInline internal</c>). Distinct from <see cref="ModuleInternal"/>
    /// so the emission-time Pattern 2 gate can be counted independently from the wrapper
    /// post-processor's existing Pattern 2 hits.
    /// </summary>
    Pattern2InternalTypeReach,
    /// <summary>
    /// A <c>public</c> member declared on a <c>@usableFromInline internal</c> parent type,
    /// where the member shape has <b>no clean direct-CallConvSwift fallback</b>: an async
    /// member (the async bridge wrapper still names the internal parent under
    /// <c>@_silgen_name</c>), a closure-bearing member (degrades to a faulting legacy
    /// CallConvSwift path), or a frozen-struct operator (a static-operator CallConvSwift
    /// P/Invoke crashes ILC on NativeAOT, so it must be a <c>@_cdecl</c> wrapper that names
    /// the parent). Because the wrapper-compilation module cannot name the internal parent
    /// and no fallback exists, the correct emission outcome is to DROP the member — distinct
    /// from <see cref="ModuleInternal"/> (the member's own access) and from the sync
    /// internal-receiver case, which <c>WrapperValidation</c> arm 2b keeps by rejecting only
    /// the wrapper and binding a direct CallConvSwift P/Invoke. Dropping here at emission is
    /// public-API-identical to the previous emit-then-strip + C# reconcile.
    /// </summary>
    ParentModuleInternalNoFallback,
    /// <summary>
    /// Member signature references a Swift type that is auto-bridged but not yet present in the
    /// .NET Foundation (or similar) assembly — e.g. <c>Foundation.LocalizedStringResource</c> in a
    /// container/closure position, or <c>Foundation.Predicate</c>. The owning module IS supported;
    /// only the individual type is unavailable in .NET. Distinct from <see cref="SwiftUIConstraint"/>
    /// (a genuine SwiftUI/Combine-module reference) and <see cref="UnsupportedType"/> (a type that
    /// merely needs exporting) so the report attributes the drop to the real cause rather than
    /// misclassifying a Foundation type as SwiftUI/Combine.
    /// </summary>
    NetUnavailableType,

    /// <summary>
    /// References a framework type that has no .NET binding at all: the type database resolves it
    /// only by synthesizing a bridged ObjC <em>class</em> record, yet the type's Swift USR proves it
    /// is a value type (struct/enum). The synthesized class reference points at a C# type the
    /// framework's binding never defines, so emitting the member would produce a CS0234 dangling
    /// reference. Distinct from <see cref="NetUnavailableType"/> (a curated Foundation type that IS
    /// available in the OS but absent from the .NET assembly) so the report — and the loud
    /// generation-time warning — attribute the drop to a genuinely unbindable cross-framework type.
    /// </summary>
    AbsentFrameworkType,

    /// <summary>
    /// An EveryProtocol reverse-dispatch surface (proxy-backed member) was <b>emitted but degraded</b>
    /// because the protocol's <c>{Protocol}Proxy</c> conformance could not be synthesized. Unlike
    /// <see cref="EveryProtocolConformanceSkipped"/> (recorded once for the suppressed proxy <em>class</em>,
    /// <see cref="BindingItemKind.Type"/>), this is recorded per <em>member</em> so the persisted report
    /// carries a durable, classified diagnostic for each degradation site instead of a silent throwing
    /// stub / dropped wrap / fail-fast warning. The concrete site is stamped into
    /// <see cref="SkippedItem.Details"/> via <c>SuppressedProxyReporting</c>:
    /// <list type="bullet">
    /// <item><b>produce-throw</b> — a getter/return that could only construct the missing proxy now emits
    /// a throwing stub (the trust failure a fail-closed 1.0 aims to eliminate; omission is the ideal but
    /// requires threading the suppressed-proxy set into member validation — see the session generalization
    /// report).</item>
    /// <item><b>consume-degraded</b> — a setter/parameter keeps working for Swift-vended conformers, but a
    /// C#-authored conformer cannot be marshalled in (no proxy to wrap it), so a reverse callback set from
    /// C# never fires.</item>
    /// <item><b>receiver-failfast</b> — a reverse-dispatch receiver fail-fasts because its existential
    /// payload references the missing proxy; the vtable slot is retained for layout parity.</item>
    /// </list>
    /// </summary>
    SuppressedProxyMemberDegraded,

    // ── ObjC binding path (mixed + pure-ObjC surfaces) ────────────────────────────────
    // A mixed (ObjC+Swift) binding — and a pure-ObjC binding — has an ObjC surface whose
    // drops the ObjC pipeline (ClangAstParser → ApiDefinitionEmitter) records as its own
    // ObjCSkipReason vocabulary. These reasons mirror that vocabulary 1:1 (mapped by
    // ObjCSkipProjection) so the ObjC drop set folds into the SAME SkipTriage/ReviewCount
    // gate as the Swift surface, instead of being invisible in any persisted artifact. Each
    // stays distinct from its Swift near-analogue (e.g. ObjCUnresolvableType vs
    // UnsupportedType) so the by-reason breakdown attributes each drop to its real cause
    // rather than relabelling an ObjC cause as a semantically-different Swift one.

    /// <summary>ObjC member dropped because a referenced type is not in the ObjC type registry.</summary>
    ObjCUnresolvableType,
    /// <summary>ObjC declaration marked unavailable on this platform (NS_UNAVAILABLE / deprecated-unavailable).</summary>
    ObjCUnavailableApi,
    /// <summary>ObjC construct not yet supported by the binding generator.</summary>
    ObjCUnsupportedConstruct,
    /// <summary>ObjC member dropped to resolve a name/accessibility conflict.</summary>
    ObjCAccessibilityConflict,
    /// <summary>ObjC member's projected C# signature collides with another member.</summary>
    ObjCDuplicateSignature,
    /// <summary>ObjC variadic function/method — not representable as a P/Invoke.</summary>
    ObjCVariadicFunction,
    /// <summary>ObjC category contributed no bindable members.</summary>
    ObjCEmptyCategory,
    /// <summary>ObjC declaration has no matching exported native symbol in any linked binary.</summary>
    ObjCMissingNativeSymbol,
    /// <summary>ObjC duplicate selector flattened to a single member across the type hierarchy.</summary>
    ObjCDuplicateSelector,

    /// <summary>
    /// Emitting this declaration threw. The exception was contained at the dispatch seam, the whole
    /// emission attempt was discarded, and the module was re-emitted with this declaration denied —
    /// so the rest of the surface still ships. Always a generator defect: the declaration is a shape
    /// the emitter mishandles rather than one it deliberately declines, which is why this reason
    /// carries the raw exception fingerprint in its details and lands in the "review" tier.
    /// </summary>
    EmitterFault,

    /// <summary>
    /// The declaration itself was never reached by a member gate: the type that declares it was
    /// suppressed as a whole (a SwiftUI <c>View</c>, an <c>@_spi</c> or underscore-internal type, a
    /// supplement-owned type, an emitter-faulted type, …), so emission skipped straight past the
    /// member loop. Recorded by the post-emission reconciliation so a member counted in
    /// <see cref="BindingReport.TotalMembers"/> is never left as neither emitted nor skipped —
    /// without it the report silently loses exactly the members that whole-type suppression removes,
    /// and every "lost surface" figure under-counts there.
    /// <para>
    /// The cause of the suppression lives on the declaring type's own skip row; this row exists to
    /// account for the member. Its actionability therefore follows the parent: a member of a type
    /// that was never public surface is itself never-public (nothing was lost), and any other
    /// suppressed parent leaves a structural loss — the member has no C# type to live on. The token
    /// that carries which of the two applies is stamped into <see cref="SkippedItem.Details"/> by
    /// <c>SuppressedParentSkipCause</c>.
    /// </para>
    /// </summary>
    ParentTypeSuppressed,

    Unknown,

    /// <summary>
    /// A protocol conformance declared by a type was dropped from the emitted C# because the
    /// conformance validator could not fully implement it: at least one requirement has no
    /// representable C# member on the conforming type (unsupported signature, unsatisfied
    /// constraint, missing extension default, …). The type itself still emits — only the
    /// <c>: I{Protocol}</c> base-list entry and the members it would have forced are absent, so
    /// consumers silently lose the ability to pass the type where the protocol is expected.
    /// <para>
    /// The row names the protocol, and <see cref="SkippedItem.Details"/> carries the FIRST unmet
    /// requirement (member kind + printed name + why) — the validator short-circuits on the first
    /// failure, so that one requirement is the whole actionable payload. Fixing it does not
    /// guarantee the conformance emits; it guarantees the next blocker becomes visible.
    /// </para>
    /// </summary>
    ConformanceNotFullyImplementable,

    /// <summary>
    /// A protocol requirement that IS declared on the emitted <c>I{Protocol}</c> interface, but whose
    /// <c>{Protocol}Proxy</c> implementation cannot reach the Swift witness — the shape has no
    /// dispatchable witness-table lowering (non-blittable parameter/return, closure parameter,
    /// mixed generic/non-generic requirement set, subscripts generally). The proxy emits a stub that
    /// throws, carrying the SB0003 <c>[Obsolete]</c> diagnostic.
    /// <para>
    /// This is a DEGRADATION, not an absence: the member is still declared and still callable on a
    /// concrete Swift-backed instance — only calls through a protocol-typed value fail. The rows are
    /// therefore appended to <see cref="BindingReport.SkippedItems"/> for countability WITHOUT being
    /// counted in <see cref="BindingReport.SkippedMembers"/>, which continues to mean "no C#
    /// declaration was written". <see cref="SkippedItem.Details"/> carries the per-site
    /// non-dispatchability reason the emitter already computed for the diagnostic text.
    /// </para>
    /// </summary>
    ProtocolWitnessNotDispatchable,

    /// <summary>
    /// The protocol's <c>{Protocol}Proxy</c> declares at least one reverse-dispatch obligation and
    /// wires NONE of them: every requirement is either absent from the vtable layout (non-dispatchable
    /// closure, method-level generic, Self-typed) or has no <c>Receive_*</c> trampoline, so the table
    /// would have been registered with Swift holding nothing but null function pointers. A C# type
    /// implementing <c>I{Protocol}</c> compiles, runs, and is never called back.
    /// <para>
    /// Like <see cref="ProtocolWitnessNotDispatchable"/> this is a DEGRADATION, not an absence — the
    /// interface and the proxy are both emitted and Swift-vended values consumed through the
    /// interface still work; only the C#-implements-the-protocol direction is dead. The row is
    /// appended to <see cref="BindingReport.SkippedItems"/> for countability WITHOUT counting in
    /// <see cref="BindingReport.SkippedMembers"/>. <see cref="SkippedItem.Details"/> carries the
    /// per-requirement dispositions, which is what turns "this interface does nothing" into a list
    /// of the specific shapes that would have to become dispatchable.
    /// </para>
    /// </summary>
    ProtocolProxyVtableEmpty,
}

/// <summary>
/// A single skipped type/member entry.
/// </summary>
public sealed class SkippedItem
{
    public required BindingItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? ContainingType { get; init; }
    public required SkipReason Reason { get; init; }
    public string? Details { get; init; }
    public string? RecommendedWorkaround { get; init; }

    /// <summary>
    /// The closed CSM projections that recover this member's consumer surface, when the skipped
    /// open-generic member is not actually unreachable. A generic-container property such as
    /// <c>MusicLibraryResponse&lt;T&gt;.items : MusicItemCollection&lt;T&gt;</c> resolves to
    /// <c>MusicItemCollection&lt;Swift.AnyType&gt;</c> on the open shell and is skipped
    /// <see cref="SkipReason.AnyTypeFallback"/> — but the concrete-specialization emitter projects a
    /// typed closed getter per conformer (<c>MusicLibraryResponse&lt;Album&gt;.Items()</c>, …), so the
    /// surface IS callable. Each entry names one such projection. Populated purely from emission facts
    /// (a CSM projection actually emitted for this base member — never a name-pattern guess), so a
    /// skip row with no <see cref="RecoveredBy"/> annotation really is unreachable. Null/empty when the
    /// skip is a genuine, unrecovered loss.
    /// </summary>
    public List<string>? RecoveredBy { get; set; }

    /// <summary>
    /// Best-effort source position tying the skip back to the swiftinterface line/column
    /// the parser saw. Null when the fact came from ABI JSON, a synthesized decl, or a
    /// dependency module without a swiftinterface input — the parser does not fabricate
    /// positions in those cases. Serialized as a structured field on
    /// <c>binding-report.json</c> rather than buried in <see cref="Details"/>.
    /// </summary>
    public SourcePosition? Position { get; init; }

    /// <summary>
    /// Canonical <see cref="BindingsGeneration.DeclId"/> of the declaration this row describes —
    /// the stable, parseable identity a consumer can key on across regenerations. Unlike
    /// <see cref="Name"/>/<see cref="ContainingType"/>, it separates overloads and accessors, so
    /// it is the field to match on when correlating a skip with a later run or with a denylist.
    /// Null only for rows recorded through a path that had no declaration in scope.
    /// </summary>
    public string? DeclId { get; init; }

    /// <summary>
    /// Canonical <see cref="RecoveryUnitId"/> of the row that ultimately caused this one — its own
    /// unit on a root cause, the root's unit on a cascade. Grouping by it turns "forty rows" into
    /// "one root and thirty-nine consequences". Null when the row carries no parseable declaration
    /// identity to name a unit with.
    /// </summary>
    /// <remarks>
    /// Settable, like <see cref="RecoveredBy"/>, because <see cref="SkipAttributionLinker"/> fills it
    /// in at projection time: the causal picture is only complete once every stage has contributed
    /// its rows, and some rows do not exist until after the Swift wrapper is built.
    /// </remarks>
    public string? RootCauseId { get; set; }

    /// <summary>
    /// Canonical <see cref="RecoveryUnitId"/> of the row this one is a direct consequence of. Null
    /// exactly when this row is itself a root cause.
    /// </summary>
    public string? CascadeFrom { get; set; }

    /// <summary>Who is in a position to fix this. Null until attribution has run.</summary>
    /// <remarks>
    /// The three attribution fields are nullable so that "not computed" is distinguishable from a
    /// computed answer of <see cref="BindingsGeneration.CauseOwner.Unknown"/>. The artifact manifest is
    /// serialized before <see cref="BindingReportProjection"/> runs — attribution cannot be computed
    /// any earlier, since rows keep arriving until the Swift wrapper is built — so these read null
    /// there and carry real values only in <c>binding-report.json</c>. A non-null default would make
    /// the manifest assert a stage and an owner it never determined.
    /// </remarks>
    public CauseOwner? CauseOwner { get; set; }

    /// <summary>The pipeline stage at which this degradation was decided. Null until attribution has run.</summary>
    public RecoveryStage? RecoveryStage { get; set; }

    /// <summary>
    /// How much to trust <see cref="CauseOwner"/> and <see cref="RecoveryStage"/>. Null until
    /// attribution has run.
    /// </summary>
    public AttributionConfidence? Confidence { get; set; }
}

/// <summary>
/// A <c>// Unsupported:</c> comment-drop, with the identity of the declaration it replaced.
/// </summary>
/// <remarks>
/// The bare description string remains the dedup key and stays in
/// <see cref="BindingReport.UnsupportedCommentDrops"/> for consumers that already read it; this
/// parallel list adds the machine-usable identity alongside the human-readable text.
/// </remarks>
public sealed class UnsupportedCommentDropItem
{
    /// <summary>The comment text, minus its leading <c>// </c> — identical to the legacy entry.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Canonical <see cref="BindingsGeneration.DeclId"/> of the dropped declaration, or null when
    /// the emitting site had no declaration in scope. Because <see cref="Description"/> is the dedup
    /// key, this names the FIRST declaration to produce that text — a representative, not an
    /// exhaustive list, whenever several declarations share one description (same-named overloads,
    /// or same-named nested types under different parents, whose comment text is identical). The
    /// per-declaration enumeration is <see cref="BindingReport.SkippedItems"/>.
    /// </summary>
    public string? DeclId { get; init; }
}

/// <summary>
/// A SwiftUI View detected for bridge generation.
/// </summary>
public sealed class BridgedViewItem
{
    public required string ViewName { get; init; }
    public required string ModuleName { get; init; }
    public required string InitClassification { get; init; }
    public required string BridgeStatus { get; init; }
}

/// <summary>
/// A theme-bridged property (Color/Font setter and optional getter generated via @_cdecl).
/// </summary>
public sealed class ThemeBridgedItem
{
    public required string ClassName { get; init; }
    public required string PropertyName { get; init; }
    public required string PropertyType { get; init; }
}

/// <summary>
/// One overload-disambiguation decision: a member whose C# name was moved off its natural
/// projection because a sibling overload projects onto the same C# signature.
///
/// <para>This is the resolver's own assignment record, and it exists so the "no bare numeric
/// suffix on the public surface" invariant can be checked against DECISIONS rather than against
/// emitted identifiers. A name-shaped check cannot tell a resolver-assigned <c>Configure2</c>
/// from a Swift author's own <c>vector3</c>; a record carrying both the natural name and the
/// assigned one can — the assignment is numeric exactly when the assigned name is the natural
/// name plus digits.</para>
/// </summary>
public sealed class OverloadRenameItem
{
    /// <summary>Declaring type's C#-visible name, or the module name for a free function.</summary>
    public required string DeclaringName { get; init; }

    /// <summary>The member's Swift signature, labels included — the call site a consumer would read.</summary>
    public required string SwiftSignature { get; init; }

    /// <summary>The C# name this member would carry if no sibling overload contested it.</summary>
    public required string NaturalName { get; init; }

    /// <summary>The C# name actually emitted.</summary>
    public required string EmittedName { get; init; }

    /// <summary>Which rung of the disambiguation ladder produced <see cref="EmittedName"/>.</summary>
    public required string Scheme { get; init; }
}

/// <summary>
/// SwiftUI bridge coverage summary for a module.
/// </summary>
public sealed class BridgeSummary
{
    public int TotalViews { get; set; }
    public int Generated { get; set; }
    public int Template { get; set; }
    public int HintSkipped { get; set; }
    public int Skipped { get; set; }
    public double GeneratedPercent { get; set; }
}

/// <summary>
/// A member that was auto-wrapped with a generated Swift wrapper + C# factory.
/// </summary>
public sealed class WrappedItem
{
    public required BindingItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? MangledName { get; init; }
    public string? ContainingType { get; init; }
    public required string WrapperKind { get; init; }
    public string? Details { get; init; }
}
