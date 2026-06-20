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
    /// Distinct <c>// Unsupported:</c> comment-drops emitted this run (Finding 53) — a type or
    /// member the generator could not bind and left as a comment in the generated C#. Each is
    /// surfaced as a loud <c>SWIFTBIND025</c> diagnostic at report time so a dropped declaration is
    /// never silent. Populated by <see cref="ReportCollector"/> from the ambient comment chokepoint.
    /// </summary>
    public List<string> UnsupportedCommentDrops { get; } = new();

    /// <summary>
    /// Distinct Swift types that degraded to bare <c>object</c> with no <c>[UnsupportedSwiftType]</c>
    /// marker (Finding 53) — e.g. an existential the resolver could not project at a closure
    /// parameter/return position. Each is surfaced as a loud <c>SWIFTBIND026</c> diagnostic so the
    /// otherwise-silent collapse to an untyped value is observable.
    /// </summary>
    public List<string> ObjectDegradations { get; } = new();

    /// <summary>
    /// Summary of SwiftUI bridge coverage for this module.
    /// Null when the module has no SwiftUI views.
    /// </summary>
    public BridgeSummary? BridgeSummary { get; set; }
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
    Unknown,
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
    /// Best-effort source position tying the skip back to the swiftinterface line/column
    /// the parser saw. Null when the fact came from ABI JSON, a synthesized decl, or a
    /// dependency module without a swiftinterface input — the parser does not fabricate
    /// positions in those cases. Serialized as a structured field on
    /// <c>binding-report.json</c> rather than buried in <see cref="Details"/>.
    /// </summary>
    public SourcePosition? Position { get; init; }
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
