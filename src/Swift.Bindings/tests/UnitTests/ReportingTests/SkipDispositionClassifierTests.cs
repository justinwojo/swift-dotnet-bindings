// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using Xunit;

namespace BindingsGeneration.Tests;

public class SkipDispositionClassifierTests
{
    /// <summary>
    /// The completeness guard: every <see cref="SkipReason"/> must have an explicit disposition. A new
    /// reason added without one still classifies (defaults to Review) but trips this test — forcing the
    /// triage knowledge to stay complete and in one place.
    /// </summary>
    [Fact]
    public void EverySkipReason_HasExplicitDisposition()
    {
        foreach (SkipReason reason in Enum.GetValues<SkipReason>())
        {
            Assert.Contains(reason, SkipDispositionClassifier.ExplicitlyClassifiedReasons);
        }
    }

    [Theory]
    [InlineData(SkipReason.ModuleInternal, SkipDisposition.ExpectedNonPublic)]
    [InlineData(SkipReason.UnderscorePrefixInternal, SkipDisposition.ExpectedNonPublic)]
    [InlineData(SkipReason.ParentModuleInternalNoFallback, SkipDisposition.ExpectedNonPublic)]
    [InlineData(SkipReason.SynthesizedCodable, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.StaticProtocolMember, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.SwiftUIView, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.Pattern2InternalTypeReach, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.OwnedByAppleSupplement, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.AncestorSkipped, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.UnsupportedExistential, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.AnyTypeFallback, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.NetUnavailableType, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.AbsentFrameworkType, SkipDisposition.KnownLimitation)]
    // Wrapper-symbol-integrity reasons: both are planning-time truthful skips (a member that
    // could never own an @_cdecl wrapper symbol), documented consumer-visible gaps → KnownLimitation.
    [InlineData(SkipReason.ConstrainedExtensionWrapper, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.GenericEnumCaseConstructor, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.SuppressedProxyMemberDegraded, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.DuplicateSignature, SkipDisposition.KnownLimitation)]
    // ObjC binding-path reasons: consumer-visible documented gaps are KnownLimitation, correct-by-design
    // structural skips are ExpectedStructural, and none default to Review (every ObjC drop is attributed).
    [InlineData(SkipReason.ObjCUnresolvableType, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.ObjCUnsupportedConstruct, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.ObjCDuplicateSignature, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.ObjCVariadicFunction, SkipDisposition.KnownLimitation)]
    [InlineData(SkipReason.ObjCUnavailableApi, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.ObjCAccessibilityConflict, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.ObjCEmptyCategory, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.ObjCDuplicateSelector, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.ObjCMissingNativeSymbol, SkipDisposition.ExpectedStructural)]
    [InlineData(SkipReason.MissingHandler, SkipDisposition.Review)]
    [InlineData(SkipReason.MissingWrapperSymbol, SkipDisposition.Review)]
    [InlineData(SkipReason.EveryProtocolConformanceSkipped, SkipDisposition.Review)]
    [InlineData(SkipReason.Unknown, SkipDisposition.Review)]
    public void Classify_Reason_MapsToExpectedTier(SkipReason reason, SkipDisposition expected)
    {
        Assert.Equal(expected, SkipDispositionClassifier.Classify(reason));
    }

    [Fact]
    public void Classify_UnmappedReasonValue_DefaultsToReview()
    {
        // A value outside the defined enum stands in for a future reason added without a disposition:
        // it must fail loud (Review), never be silently called "expected".
        Assert.Equal(SkipDisposition.Review, SkipDispositionClassifier.Classify((SkipReason)9999));
    }

    [Fact]
    public void Classify_NonEveryProtocolItem_UsesReasonAndIgnoresDetails()
    {
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Method,
            Name = "Foo",
            Reason = SkipReason.ModuleInternal,
            // Details that would map to Review under the EveryProtocol path — must be ignored here.
            Details = "no decision recorded",
        };

        Assert.Equal(SkipDisposition.ExpectedNonPublic, SkipDispositionClassifier.Classify(item));
    }

    [Theory]
    [InlineData("Protocol proxy skipped: EveryProtocol conformance was not emitted (module-internal protocol).", SkipDisposition.ExpectedNonPublic)]
    [InlineData("Protocol proxy skipped: EveryProtocol conformance was not emitted (associated-type or Self-constrained protocol).", SkipDisposition.ExpectedStructural)]
    [InlineData("Protocol proxy skipped: EveryProtocol conformance was not emitted (StaticMethodRequirements).", SkipDisposition.ExpectedStructural)]
    [InlineData("Protocol proxy skipped: EveryProtocol conformance was not emitted (ClassSuperclassRequired).", SkipDisposition.ExpectedStructural)]
    [InlineData("Protocol proxy skipped: EveryProtocol conformance was not emitted (no decision recorded).", SkipDisposition.Review)]
    public void Classify_EveryProtocolItem_RefinesFromDetails(string details, SkipDisposition expected)
    {
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "SomeProxy",
            Reason = SkipReason.EveryProtocolConformanceSkipped,
            Details = details,
        };

        Assert.Equal(expected, SkipDispositionClassifier.Classify(item));
    }

    [Fact]
    public void Classify_ItemWithRecoveredBy_IsRecovered_OverridingReason()
    {
        // An AnyTypeFallback skip (normally KnownLimitation) whose typed surface was recovered by CSM
        // concrete-specialization projections must classify as Recovered — the least-actionable tier.
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Property,
            Name = "items",
            ContainingType = "MusicItemCollection",
            Reason = SkipReason.AnyTypeFallback,
            RecoveredBy = new System.Collections.Generic.List<string> { "MusicItemCollection<Song>.Items" },
        };

        Assert.Equal(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(item));
    }

    [Fact]
    public void Classify_ItemWithEmptyRecoveredBy_FallsBackToReason()
    {
        // An empty (or absent) recovery list is NOT a recovery — the row stays a plain skip so the
        // reader-facing invariant holds: "a row that says skipped with no annotation really is unreachable."
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Property,
            Name = "items",
            ContainingType = "MusicItemCollection",
            Reason = SkipReason.AnyTypeFallback,
            RecoveredBy = new System.Collections.Generic.List<string>(),
        };

        Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
    }

    [Fact]
    public void Classify_EveryProtocolItem_NullDetails_IsReview()
    {
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "SomeProxy",
            Reason = SkipReason.EveryProtocolConformanceSkipped,
            Details = null,
        };

        Assert.Equal(SkipDisposition.Review, SkipDispositionClassifier.Classify(item));
    }

    // ── EveryProtocolSkipCause: writer/reader vocabulary ─────────────────────────────────────

    [Fact]
    public void ForDroppedProtocol_ModuleInternal_WinsOverShape()
    {
        var protocolDecl = Protocol(isModuleInternal: true, hasSelf: true, associatedTypes: 1);
        Assert.Equal(EveryProtocolSkipCause.ModuleInternal, EveryProtocolSkipCause.ForDroppedProtocol(protocolDecl));
    }

    [Fact]
    public void ForDroppedProtocol_AssociatedType_IsStructuralCause()
    {
        var protocolDecl = Protocol(isModuleInternal: false, hasSelf: false, associatedTypes: 1);
        Assert.Equal(EveryProtocolSkipCause.AssociatedTypeOrSelf, EveryProtocolSkipCause.ForDroppedProtocol(protocolDecl));
    }

    [Fact]
    public void ForDroppedProtocol_SelfRequirement_IsStructuralCause()
    {
        var protocolDecl = Protocol(isModuleInternal: false, hasSelf: true, associatedTypes: 0);
        Assert.Equal(EveryProtocolSkipCause.AssociatedTypeOrSelf, EveryProtocolSkipCause.ForDroppedProtocol(protocolDecl));
    }

    [Fact]
    public void ForDroppedProtocol_PlainPublic_IsUnexplained()
    {
        var protocolDecl = Protocol(isModuleInternal: false, hasSelf: false, associatedTypes: 0);
        Assert.Equal(EveryProtocolSkipCause.NoDecisionRecorded, EveryProtocolSkipCause.ForDroppedProtocol(protocolDecl));
    }

    /// <summary>
    /// The end-to-end contract: the cause string the writer stamps into Details must classify to the
    /// intended tier when read back. Internal → nothing-to-do; a plain public protocol dropped for an
    /// unidentified cause → look at it.
    /// </summary>
    [Theory]
    [InlineData(true, false, 0, SkipDisposition.ExpectedNonPublic)]
    [InlineData(false, false, 1, SkipDisposition.ExpectedStructural)]
    [InlineData(false, true, 0, SkipDisposition.ExpectedStructural)]
    [InlineData(false, false, 0, SkipDisposition.Review)]
    public void ForDroppedProtocol_RoundTripsThroughDetailsToDisposition(
        bool isModuleInternal, bool hasSelf, int associatedTypes, SkipDisposition expected)
    {
        var cause = EveryProtocolSkipCause.ForDroppedProtocol(
            Protocol(isModuleInternal, hasSelf, associatedTypes));
        // Mirror the exact Details template ProtocolHandler emits.
        var details = $"Protocol proxy skipped: EveryProtocol conformance was not emitted ({cause}).";

        Assert.Equal(expected, EveryProtocolSkipCause.ClassifyDisposition(details));
    }

    /// <summary>
    /// Every dropped-from-candidacy structural cause (mechanism D) must round-trip through the Details
    /// template to <see cref="SkipDisposition.ExpectedStructural"/> — the whole point of attributing the
    /// drop instead of falling back to "no decision recorded" (Review). A cause that accidentally
    /// contained the "no decision recorded" or "module-internal protocol" substring would misclassify,
    /// so this doubles as a guard on the token vocabulary.
    /// </summary>
    [Theory]
    [InlineData(EveryProtocolSkipCause.DroppedForeignProtocol)]
    [InlineData(EveryProtocolSkipCause.DroppedClassIdentity)]
    [InlineData(EveryProtocolSkipCause.DroppedClassSuperclass)]
    [InlineData(EveryProtocolSkipCause.DroppedInheritsUnsatisfiable)]
    [InlineData(EveryProtocolSkipCause.DroppedInternalTypeReach)]
    [InlineData(EveryProtocolSkipCause.DroppedPropertyTypeConflict)]
    [InlineData(EveryProtocolSkipCause.DroppedMemberKindConflict)]
    [InlineData(EveryProtocolSkipCause.DroppedCandidacyStructural)]
    public void DroppedCandidacyCause_ClassifiesStructural_NotReview(string cause)
    {
        var details = $"Protocol proxy skipped: EveryProtocol conformance was not emitted ({cause}).";
        Assert.Equal(SkipDisposition.ExpectedStructural, EveryProtocolSkipCause.ClassifyDisposition(details));
    }

    // ── SuppressedParentSkipCause: writer/reader vocabulary ──────────────────────────────────

    /// <summary>
    /// A member accounted for because its declaring type was suppressed inherits the parent's tier:
    /// a never-public parent means nothing public was lost, any other suppressed parent means the
    /// member has no C# type left to live on. Both halves have to survive the Details round-trip, or
    /// the accounting rows land in the wrong bucket and the "lost public surface" figure moves in a
    /// direction the suppression never justified.
    /// </summary>
    [Theory]
    [InlineData(true, SkipDisposition.ExpectedNonPublic)]
    [InlineData(false, SkipDisposition.ExpectedStructural)]
    public void SuppressedParentCause_RoundTripsThroughDetails(bool parentNeverPublic, SkipDisposition expected)
    {
        var details = SuppressedParentSkipCause.Format("Demo.Widget", SkipReason.SwiftUIView, parentNeverPublic);
        Assert.Equal(expected, SuppressedParentSkipCause.ClassifyDisposition(details));
    }

    /// <summary>
    /// The reader is fail-loud: details it cannot recognise mean the tier was never recorded, which is
    /// exactly the "someone needs to look at this" case — silently guessing a benign tier would hide a
    /// broken writer behind a clean report.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated explanation")]
    public void SuppressedParentCause_UnrecognizedDetails_ClassifyReview(string? details)
    {
        Assert.Equal(SkipDisposition.Review, SuppressedParentSkipCause.ClassifyDisposition(details));
    }

    /// <summary>
    /// The per-item overload is what the report actually calls; it must consult the recorded cause
    /// rather than the reason-only table, which cannot tell the two tiers apart.
    /// </summary>
    [Fact]
    public void ClassifyItem_ParentTypeSuppressed_PrefersTheRecordedCauseOverTheReasonDefault()
    {
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Method,
            Name = "doWork",
            ContainingType = "Demo.Widget",
            Reason = SkipReason.ParentTypeSuppressed,
            Details = SuppressedParentSkipCause.Format("Demo.Widget", SkipReason.ModuleInternal, parentNeverPublic: true),
        };

        Assert.Equal(SkipDisposition.ExpectedNonPublic, SkipDispositionClassifier.Classify(item));
        Assert.Equal(SkipDisposition.ExpectedStructural, SkipDispositionClassifier.Classify(item.Reason));
    }

    private static ProtocolDecl Protocol(bool isModuleInternal, bool hasSelf, int associatedTypes)
    {
        var associated = new System.Collections.Generic.List<AssociatedTypeDecl>();
        for (int i = 0; i < associatedTypes; i++)
            associated.Add(new AssociatedTypeDecl { Name = $"T{i}" });

        return new ProtocolDecl
        {
            Name = "P",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Demo.P"),
            MangledName = "$s4Demo1PP",
            Properties = new System.Collections.Generic.List<PropertyDecl>(),
            Methods = new System.Collections.Generic.List<MethodDecl>(),
            Types = new System.Collections.Generic.List<TypeDecl>(),
            Operators = new System.Collections.Generic.List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsModuleInternal = isModuleInternal,
            HasSelfRequirement = hasSelf,
            AssociatedTypes = associated,
        };
    }
}
