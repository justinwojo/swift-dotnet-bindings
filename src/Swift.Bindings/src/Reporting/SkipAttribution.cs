// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>Who is in a position to fix a degradation.</summary>
/// <remarks>
/// Ownership, not blame. The question this answers is "whose change would make this row go away",
/// which is what a reader of the report actually needs in order to act on it.
/// </remarks>
public enum CauseOwner
{
    /// <summary>
    /// No owner could be determined. The conservative default for a reason with no rule — deliberately
    /// distinct from <see cref="Generator"/> so "we never classified this" cannot be misread as "we
    /// looked and it is a known generator limitation".
    /// </summary>
    Unknown,

    /// <summary>How the binding was invoked or packaged — options, framework slices, package split.</summary>
    InputConfiguration,

    /// <summary>The Swift library's own API surface — internal types reached from public API, unavailable APIs.</summary>
    LibraryAuthor,

    /// <summary>This generator: a capability it does not yet have, or a defect in what it emits.</summary>
    Generator,

    /// <summary>The Swift compiler or its runtime.</summary>
    SwiftToolchain,

    /// <summary>The .NET runtime, SDK, or its type system.</summary>
    DotNetToolchain,

    /// <summary>The build environment — SDK versions, available frameworks, host state.</summary>
    Environment,
}

/// <summary>The pipeline stage at which a degradation was decided.</summary>
public enum RecoveryStage
{
    /// <summary>Reading the ABI JSON and swiftinterface — facts about the input.</summary>
    Parse,

    /// <summary>Deciding what to bind. Where most capability declines are made.</summary>
    Plan,

    /// <summary>Generating C# and Swift text.</summary>
    Emit,

    /// <summary>Compiling the generated Swift wrapper.</summary>
    SwiftCompile,

    /// <summary>Compiling the generated C#.</summary>
    CSharpCompile,

    /// <summary>Checking the emitted binding against the library's ABI.</summary>
    AbiValidation,

    /// <summary>Checking that referenced native symbols actually exist in the binary.</summary>
    SymbolValidation,
}

/// <summary>How much to trust an attribution.</summary>
public enum AttributionConfidence
{
    /// <summary>Inferred or defaulted; the reason alone did not determine the answer.</summary>
    Low,

    /// <summary>The reason determines the answer for most rows, but context can change it.</summary>
    Medium,

    /// <summary>The reason determines the answer.</summary>
    High,
}

/// <summary>One row's attribution: who owns it, where it happened, how sure we are.</summary>
public readonly record struct SkipAttribution
{
    /// <summary>Who could fix it.</summary>
    public required CauseOwner Owner { get; init; }

    /// <summary>Where in the pipeline it was decided.</summary>
    public required RecoveryStage Stage { get; init; }

    /// <summary>How much to trust this attribution.</summary>
    public required AttributionConfidence Confidence { get; init; }

    /// <summary>Builds an attribution.</summary>
    public static SkipAttribution Of(CauseOwner owner, RecoveryStage stage, AttributionConfidence confidence) =>
        new() { Owner = owner, Stage = stage, Confidence = confidence };
}

/// <summary>
/// Maps a <see cref="SkipReason"/> to its <em>root-cause</em> attribution.
/// </summary>
/// <remarks>
/// <para>
/// This table is a default for rows that are roots. A cascade row must not be classified from its own
/// reason — <see cref="SkipReason.AncestorSkipped"/> says nothing about who owns the failure, only
/// that something above it failed — so <see cref="SkipAttributionLinker"/> resolves roots first and
/// has cascades inherit. Two reasons are also context-dependent enough that the reason alone is a
/// weak signal, and they are marked <see cref="AttributionConfidence.Low"/> rather than given a
/// confident answer the details might contradict.
/// </para>
/// <para>
/// Completeness is enforced by test, not by the compiler, following
/// <see cref="SkipDispositionClassifier"/>: every <see cref="SkipReason"/> must appear in
/// <see cref="ExplicitlyClassifiedReasons"/>. A reason with no rule classifies as
/// <see cref="CauseOwner.Unknown"/> at <see cref="AttributionConfidence.Low"/> — never as a settled
/// generator limitation, because silently promoting an unanticipated failure to "known limitation" is
/// how a real defect stops being counted as one.
/// </para>
/// </remarks>
public static class SkipCauseClassifier
{
    private static readonly ImmutableDictionary<SkipReason, SkipAttribution> Table = BuildTable();

    /// <summary>Every reason with an explicit rule. The completeness test asserts this covers the enum.</summary>
    public static IReadOnlyCollection<SkipReason> ExplicitlyClassifiedReasons => Table.Keys.ToImmutableArray();

    /// <summary>
    /// The attribution an unclassified reason receives. Public so the completeness test can assert the
    /// fallback is genuinely conservative rather than re-deriving the expectation.
    /// </summary>
    public static SkipAttribution Fallback =>
        SkipAttribution.Of(CauseOwner.Unknown, RecoveryStage.Plan, AttributionConfidence.Low);

    /// <summary>Classifies a reason as a root cause.</summary>
    public static SkipAttribution Classify(SkipReason reason) =>
        Table.TryGetValue(reason, out var attribution) ? attribution : Fallback;

    /// <summary>
    /// Classifies a root row, refining the reason-only answer where the details disambiguate the
    /// stage. <see cref="SkipReason.EmitterFault"/> covers four cases distinguished only by the row's
    /// details wording: a live emitter exception (decided at <see cref="RecoveryStage.Emit"/>); a
    /// wrapper verify-recover withdrawal — the emitter lowered the declaration fine and it was
    /// withdrawn because the compiled Swift wrapper failed, so the decision belongs to
    /// <see cref="RecoveryStage.SwiftCompile"/>; a C# verify-recover withdrawal — lowered fine and
    /// withdrawn because the emitted C# failed to compile, one rung later at
    /// <see cref="RecoveryStage.CSharpCompile"/>; and an ABI verify-recover withdrawal — lowered fine and
    /// withdrawn because typed plan-vs-descriptor validation flagged its native call, decided at
    /// <see cref="RecoveryStage.AbiValidation"/>. Each withdrawal's only surviving signal on the row is
    /// its details prefix, single-sourced on <see cref="EmitterFaultRecord"/>.
    /// </summary>
    public static SkipAttribution Classify(SkipReason reason, string? details)
    {
        var attribution = Classify(reason);
        if (reason == SkipReason.EmitterFault && details is not null)
        {
            if (details.StartsWith(EmitterFaultRecord.WithdrawalDetailsPrefix, StringComparison.Ordinal))
            {
                attribution = SkipAttribution.Of(
                    attribution.Owner, RecoveryStage.SwiftCompile, attribution.Confidence);
            }
            else if (details.StartsWith(EmitterFaultRecord.CSharpWithdrawalDetailsPrefix, StringComparison.Ordinal))
            {
                attribution = SkipAttribution.Of(
                    attribution.Owner, RecoveryStage.CSharpCompile, attribution.Confidence);
            }
            else if (details.StartsWith(EmitterFaultRecord.AbiWithdrawalDetailsPrefix, StringComparison.Ordinal))
            {
                attribution = SkipAttribution.Of(
                    attribution.Owner, RecoveryStage.AbiValidation, attribution.Confidence);
            }
        }

        return attribution;
    }

    private static ImmutableDictionary<SkipReason, SkipAttribution> BuildTable()
    {
        var builder = ImmutableDictionary.CreateBuilder<SkipReason, SkipAttribution>();

        void Add(SkipReason reason, CauseOwner owner, RecoveryStage stage, AttributionConfidence confidence) =>
            builder.Add(reason, SkipAttribution.Of(owner, stage, confidence));

        // ── Generator capability declines, decided while planning ────────────────────────────────
        // The generator cannot yet lower this shape. Nobody else can act on these.
        Add(SkipReason.UnsupportedType, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.AnyTypeFallback, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.AsyncProperty, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.SwiftUIConstraint, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.CombineFramework, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.GenericProtocolConstraint, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.UnsatisfiedGenericConstraint, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.UnsupportedSignature, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.UnsupportedExistential, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.UnsupportedClosure, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.UnsupportedAsyncStream, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.UnsupportedThrowingAsyncStream, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.MissingHandler, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.SwiftUIView, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.StaticProtocolMember, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.GenericTypeCallback, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.ActorIsolatedAsyncStream, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.SynthesizedCodable, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.ExtensionDefault, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.NonBlittableCallConvSwift, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.IndeterminatePwtShape, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.IndeterminateStructLayout, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.ActorIsolatedConstructor, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.ConstrainedExtensionWrapper, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.GenericEnumCaseConstructor, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.CovariantReturnNotRepresentable, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);

        // Two Swift declarations that project onto one C# signature. The library's API is legal; it is
        // our projection that collides, so the generator owns it.
        Add(SkipReason.DuplicateSignature, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);

        // ── Emission-stage generator declines ────────────────────────────────────────────────────
        Add(SkipReason.SuppressedProxyMethodBody, CauseOwner.Generator, RecoveryStage.Emit, AttributionConfidence.Medium);

        // An unhandled exception escaped the emitter while lowering this declaration. Nothing about the
        // library caused it and nothing else could have prevented it, so attribution is certain even
        // though the specific defect is not yet known.
        Add(SkipReason.EmitterFault, CauseOwner.Generator, RecoveryStage.Emit, AttributionConfidence.High);

        // Context-dependent: the reason alone does not say which of several causes applied, which is
        // why SkipDispositionClassifier already has to read the whole Details string to bucket it.
        Add(SkipReason.EveryProtocolConformanceSkipped, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Low);
        Add(SkipReason.SuppressedProxyMemberDegraded, CauseOwner.Generator, RecoveryStage.Emit, AttributionConfidence.Low);

        // ── Library API surface — the author could change this, we cannot ────────────────────────
        Add(SkipReason.UnderscorePrefixInternal, CauseOwner.LibraryAuthor, RecoveryStage.Parse, AttributionConfidence.High);
        Add(SkipReason.ModuleInternal, CauseOwner.LibraryAuthor, RecoveryStage.Parse, AttributionConfidence.High);
        Add(SkipReason.Pattern2InternalTypeReach, CauseOwner.LibraryAuthor, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.ParentModuleInternalNoFallback, CauseOwner.LibraryAuthor, RecoveryStage.Plan, AttributionConfidence.Medium);

        // ── Packaging / configuration ────────────────────────────────────────────────────────────
        // Not a loss: the surface is bound, by the Apple supplement package instead of here.
        Add(SkipReason.OwnedByAppleSupplement, CauseOwner.InputConfiguration, RecoveryStage.Plan, AttributionConfidence.High);

        // ── Toolchain and environment ────────────────────────────────────────────────────────────
        Add(SkipReason.NetUnavailableType, CauseOwner.DotNetToolchain, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.AbsentFrameworkType, CauseOwner.Environment, RecoveryStage.Plan, AttributionConfidence.Medium);

        // Discovered only after the Swift wrapper is built, when the symbol turns out not to exist.
        Add(SkipReason.MissingWrapperSymbol, CauseOwner.Generator, RecoveryStage.SymbolValidation, AttributionConfidence.High);

        // ── ObjC bridge ──────────────────────────────────────────────────────────────────────────
        Add(SkipReason.ObjCUnresolvableType, CauseOwner.Generator, RecoveryStage.Parse, AttributionConfidence.Medium);
        Add(SkipReason.ObjCUnavailableApi, CauseOwner.LibraryAuthor, RecoveryStage.Parse, AttributionConfidence.High);
        Add(SkipReason.ObjCUnsupportedConstruct, CauseOwner.Generator, RecoveryStage.Parse, AttributionConfidence.High);
        Add(SkipReason.ObjCAccessibilityConflict, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.ObjCDuplicateSignature, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);
        Add(SkipReason.ObjCVariadicFunction, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.High);
        Add(SkipReason.ObjCEmptyCategory, CauseOwner.LibraryAuthor, RecoveryStage.Parse, AttributionConfidence.High);
        Add(SkipReason.ObjCMissingNativeSymbol, CauseOwner.LibraryAuthor, RecoveryStage.SymbolValidation, AttributionConfidence.Medium);
        Add(SkipReason.ObjCDuplicateSelector, CauseOwner.Generator, RecoveryStage.Plan, AttributionConfidence.Medium);

        // ── Rows whose own reason carries no attribution ─────────────────────────────────────────
        // A cascade inherits from its root; when the root cannot be resolved these stay deliberately
        // unattributed rather than borrowing an answer from the reason.
        Add(SkipReason.AncestorSkipped, CauseOwner.Unknown, RecoveryStage.Plan, AttributionConfidence.Low);
        Add(SkipReason.Unknown, CauseOwner.Unknown, RecoveryStage.Plan, AttributionConfidence.Low);

        return builder.ToImmutable();
    }
}
