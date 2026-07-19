// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Cause attribution and root/cascade linking on the settled skip list.
/// </summary>
public class SkipAttributionTests
{
    private static SkippedItem Row(
        SkipReason reason,
        string name,
        BindingItemKind kind = BindingItemKind.Method,
        DeclId? decl = null,
        string? containingType = null) =>
        new()
        {
            Kind = kind,
            Name = name,
            ContainingType = containingType,
            Reason = reason,
            DeclId = decl?.Canonical,
        };

    private static DeclId TypeDecl(string name, string declPath = "") =>
        DeclId.Create("M", declPath, BindingItemKind.Type, name);

    // ── Classifier completeness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every reason must have an explicit rule. A new reason without one still classifies — as
    /// Unknown at low confidence — but trips this test, so the attribution knowledge cannot silently
    /// fall behind the enum.
    /// </summary>
    [Fact]
    public void EverySkipReason_HasAnExplicitAttribution()
    {
        foreach (SkipReason reason in Enum.GetValues<SkipReason>())
            Assert.Contains(reason, SkipCauseClassifier.ExplicitlyClassifiedReasons);
    }

    /// <summary>
    /// An unanticipated failure must never be promoted to a settled generator limitation — that is how
    /// a real defect stops being counted as one.
    /// </summary>
    [Fact]
    public void UnclassifiedReason_FallsBackToUnknownAtLowConfidence()
    {
        var fallback = SkipCauseClassifier.Classify((SkipReason)9999);

        Assert.Equal(CauseOwner.Unknown, fallback.Owner);
        Assert.Equal(AttributionConfidence.Low, fallback.Confidence);
        Assert.Equal(SkipCauseClassifier.Fallback, fallback);
    }

    [Theory]
    [InlineData(SkipReason.UnsupportedClosure, CauseOwner.Generator)]
    [InlineData(SkipReason.ModuleInternal, CauseOwner.LibraryAuthor)]
    [InlineData(SkipReason.UnderscorePrefixInternal, CauseOwner.LibraryAuthor)]
    [InlineData(SkipReason.OwnedByAppleSupplement, CauseOwner.InputConfiguration)]
    [InlineData(SkipReason.NetUnavailableType, CauseOwner.DotNetToolchain)]
    [InlineData(SkipReason.AbsentFrameworkType, CauseOwner.Environment)]
    public void Classify_AssignsTheOwnerWhoCouldActOnIt(SkipReason reason, CauseOwner expected)
    {
        Assert.Equal(expected, SkipCauseClassifier.Classify(reason).Owner);
    }

    /// <summary>
    /// Wrapper symbols are only discovered to be missing once the Swift wrapper has been built, so the
    /// stage is symbol validation rather than planning.
    /// </summary>
    [Fact]
    public void Classify_PlacesMissingWrapperSymbolAtSymbolValidation()
    {
        Assert.Equal(RecoveryStage.SymbolValidation, SkipCauseClassifier.Classify(SkipReason.MissingWrapperSymbol).Stage);
    }

    /// <summary>
    /// These reasons are context-dependent — the existing disposition classifier has to read the whole
    /// details string to bucket them — so the reason alone must not claim a confident attribution.
    /// </summary>
    [Theory]
    [InlineData(SkipReason.EveryProtocolConformanceSkipped)]
    [InlineData(SkipReason.SuppressedProxyMemberDegraded)]
    [InlineData(SkipReason.AncestorSkipped)]
    [InlineData(SkipReason.Unknown)]
    public void Classify_DoesNotClaimConfidenceItDoesNotHave(SkipReason reason)
    {
        Assert.Equal(AttributionConfidence.Low, SkipCauseClassifier.Classify(reason).Confidence);
    }

    // ── Linking ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_MarksAnOrdinarySkipAsItsOwnRoot()
    {
        var decl = DeclId.Create("M", "T", BindingItemKind.Method, "foo");
        var item = Row(SkipReason.UnsupportedClosure, "foo", decl: decl);

        SkipAttributionLinker.Link(new[] { item });

        Assert.Null(item.CascadeFrom);
        Assert.NotNull(item.RootCauseId);
        Assert.Equal(RecoveryUnitId.Create(decl, RecoveryScope.LeafApi).Canonical, item.RootCauseId);
        Assert.Equal(CauseOwner.Generator, item.CauseOwner);
    }

    [Fact]
    public void Link_LinksANestedTypeToItsSkippedAncestor()
    {
        var outer = TypeDecl("Outer");
        var inner = TypeDecl("Inner", "Outer");
        var root = Row(SkipReason.IndeterminateStructLayout, "Outer", BindingItemKind.Type, outer);
        var cascade = Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, inner);

        SkipAttributionLinker.Link(new[] { root, cascade });

        var rootUnit = RecoveryUnitId.Create(outer, RecoveryScope.TypeSurface).Canonical;
        Assert.Null(root.CascadeFrom);
        Assert.Equal(rootUnit, root.RootCauseId);
        Assert.Equal(rootUnit, cascade.CascadeFrom);
        Assert.Equal(rootUnit, cascade.RootCauseId);
    }

    /// <summary>
    /// A cascade inherits who owns the failure from its root — its own reason says only that something
    /// above it failed, never who could fix it.
    /// </summary>
    [Fact]
    public void Link_HasACascadeInheritOwnershipFromItsRoot()
    {
        var outer = TypeDecl("Outer");
        var inner = TypeDecl("Inner", "Outer");
        var root = Row(SkipReason.ModuleInternal, "Outer", BindingItemKind.Type, outer);
        var cascade = Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, inner);

        SkipAttributionLinker.Link(new[] { root, cascade });

        Assert.Equal(CauseOwner.LibraryAuthor, cascade.CauseOwner);
        Assert.Equal(RecoveryStage.Parse, cascade.RecoveryStage);
        // Second-hand evidence never claims the root's certainty.
        Assert.Equal(AttributionConfidence.High, root.Confidence);
        Assert.Equal(AttributionConfidence.Medium, cascade.Confidence);
    }

    /// <summary>Three deep: each row links to its nearest ancestor, but all resolve to one root.</summary>
    [Fact]
    public void Link_ResolvesATransitiveChainToASingleRoot()
    {
        var outer = TypeDecl("Outer");
        var mid = TypeDecl("Mid", "Outer");
        var inner = TypeDecl("Inner", "Outer.Mid");
        var rootRow = Row(SkipReason.IndeterminateStructLayout, "Outer", BindingItemKind.Type, outer);
        var midRow = Row(SkipReason.AncestorSkipped, "Mid", BindingItemKind.Type, mid);
        var innerRow = Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, inner);

        SkipAttributionLinker.Link(new[] { rootRow, midRow, innerRow });

        var rootUnit = RecoveryUnitId.Create(outer, RecoveryScope.TypeSurface).Canonical;
        var midUnit = RecoveryUnitId.Create(mid, RecoveryScope.TypeSurface).Canonical;

        Assert.Equal(rootUnit, midRow.CascadeFrom);
        Assert.Equal(midUnit, innerRow.CascadeFrom);
        Assert.Equal(rootUnit, innerRow.RootCauseId);
        Assert.Equal(rootUnit, midRow.RootCauseId);
    }

    /// <summary>
    /// A member skip is not a consequence of its containing type merely because both were skipped. A
    /// type-level skip returns before its members are walked, so co-presence is not causality — and a
    /// suppressed proxy is recorded as a synthetic type row under the protocol, where the containing
    /// type is emphatically not the cause.
    /// </summary>
    [Fact]
    public void Link_DoesNotInventACascadeFromContainingTypeCoPresence()
    {
        var type = TypeDecl("T");
        var member = DeclId.Create("M", "T", BindingItemKind.Method, "foo");
        var typeRow = Row(SkipReason.IndeterminateStructLayout, "T", BindingItemKind.Type, type);
        var memberRow = Row(SkipReason.UnsupportedClosure, "foo", decl: member, containingType: "T");

        SkipAttributionLinker.Link(new[] { typeRow, memberRow });

        Assert.Null(memberRow.CascadeFrom);
        Assert.Equal(CauseOwner.Generator, memberRow.CauseOwner);
    }

    /// <summary>
    /// Suppressed-proxy declines name their cause only in prose today, so they stay roots rather than
    /// being linked on a string match.
    /// </summary>
    [Fact]
    public void Link_LeavesSuppressedProxyRowsAsRoots()
    {
        var protocolRow = Row(SkipReason.EveryProtocolConformanceSkipped, "PProxy", BindingItemKind.Type,
            TypeDecl("P"), containingType: "P");
        var degraded = Row(SkipReason.SuppressedProxyMemberDegraded, "callback",
            decl: DeclId.Create("M", "T", BindingItemKind.Method, "callback"));

        SkipAttributionLinker.Link(new[] { protocolRow, degraded });

        Assert.Null(degraded.CascadeFrom);
        Assert.Equal(AttributionConfidence.Low, degraded.Confidence);
    }

    [Fact]
    public void Link_ToleratesRowsWithNoDeclarationIdentity()
    {
        var orphan = Row(SkipReason.MissingWrapperSymbol, "stripped");

        SkipAttributionLinker.Link(new[] { orphan });

        Assert.Null(orphan.RootCauseId);
        Assert.Null(orphan.CascadeFrom);
        Assert.Equal(RecoveryStage.SymbolValidation, orphan.RecoveryStage);
    }

    /// <summary>A report may be projected more than once; the second pass must not contradict the first.</summary>
    [Fact]
    public void Link_IsIdempotent()
    {
        var outer = TypeDecl("Outer");
        var inner = TypeDecl("Inner", "Outer");
        var rows = new[]
        {
            Row(SkipReason.IndeterminateStructLayout, "Outer", BindingItemKind.Type, outer),
            Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, inner),
        };

        SkipAttributionLinker.Link(rows);
        var snapshot = rows.Select(r => (r.RootCauseId, r.CascadeFrom, r.CauseOwner, r.RecoveryStage, r.Confidence)).ToList();

        SkipAttributionLinker.Link(rows);
        var again = rows.Select(r => (r.RootCauseId, r.CascadeFrom, r.CauseOwner, r.RecoveryStage, r.Confidence)).ToList();

        Assert.Equal(snapshot, again);
    }

    [Fact]
    public void Link_OnAnEmptyListIsANoOp()
    {
        SkipAttributionLinker.Link(Array.Empty<SkippedItem>());
    }

    /// <summary>
    /// A row whose surface was closed by another mechanism is not a degradation. Counting it would
    /// re-open something already fixed.
    /// </summary>
    [Fact]
    public void IsLoss_ExcludesRowsRecoveredByAnotherMechanism()
    {
        var lost = Row(SkipReason.UnsupportedClosure, "foo");
        var recovered = Row(SkipReason.UnsupportedClosure, "bar");
        recovered.RecoveredBy = new List<string> { "ConcreteSpecialization" };

        Assert.True(SkipAttributionLinker.IsLoss(lost));
        Assert.False(SkipAttributionLinker.IsLoss(recovered));
    }

    /// <summary>
    /// Surface deliberately left to the Apple supplement package is bound — by someone else. Counting
    /// it as a degradation would report already-provided API as missing.
    /// </summary>
    [Fact]
    public void IsLoss_ExcludesSurfaceOwnedByTheAppleSupplement()
    {
        Assert.False(SkipAttributionLinker.IsLoss(Row(SkipReason.OwnedByAppleSupplement, "UIView")));
    }

    /// <summary>
    /// Only a type can enclose a declaration. A skipped method whose own qualified path happens to
    /// prefix the row's is not its ancestor, however well the two strings line up.
    /// </summary>
    [Fact]
    public void Link_DoesNotTreatANonTypeRowAsAnAncestor()
    {
        var method = Row(
            SkipReason.UnsupportedClosure,
            "Inner",
            BindingItemKind.Method,
            DeclId.Create("M", "Outer", BindingItemKind.Method, "Inner"));
        var nested = Row(
            SkipReason.AncestorSkipped,
            "Deep",
            BindingItemKind.Type,
            TypeDecl("Deep", "Outer.Inner"));

        SkipAttributionLinker.Link(new[] { method, nested });

        Assert.Null(nested.CascadeFrom);
        // Its own root: nothing above it was eligible to be the cause.
        Assert.True(RecoveryUnitId.TryParse(nested.RootCauseId, out var unit));
        Assert.Equal(TypeDecl("Deep", "Outer.Inner"), unit.Decl);
    }

    /// <summary>
    /// A cascade edge is written with one unit id and resolved with another lookup; the two must key
    /// on the same surface. Asserting the edge equals the parent's own root id pins that agreement, so
    /// a future normalization applied on only one side fails here rather than silently making every
    /// cascade its own root.
    /// </summary>
    [Fact]
    public void Link_WritesCascadeEdgesOnTheSameKeySurfaceItResolvesThem()
    {
        var outer = Row(SkipReason.IndeterminateStructLayout, "Outer", BindingItemKind.Type, TypeDecl("Outer"));
        var inner = Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, TypeDecl("Inner", "Outer"));

        SkipAttributionLinker.Link(new[] { outer, inner });

        Assert.Equal(outer.RootCauseId, inner.CascadeFrom);
        Assert.Equal(outer.RootCauseId, inner.RootCauseId);
    }

    /// <summary>
    /// The join between the report and the recovery graph: a root-cause id must parse back into a unit
    /// id, or grouping by it is grouping by an opaque string.
    /// </summary>
    [Fact]
    public void RootCauseId_ParsesBackIntoARecoveryUnitId()
    {
        var decl = TypeDecl("T");
        var item = Row(SkipReason.IndeterminateStructLayout, "T", BindingItemKind.Type, decl);

        SkipAttributionLinker.Link(new[] { item });

        Assert.True(RecoveryUnitId.TryParse(item.RootCauseId, out var unit));
        Assert.Equal(RecoveryScope.TypeSurface, unit.Scope);
        Assert.Equal(decl, unit.Decl);
    }

    /// <summary>
    /// The attribution fields ship in both on-disk artifacts, so they have to survive the round trip
    /// the store actually performs — enums as strings, nulls preserved rather than dropped.
    /// </summary>
    [Fact]
    public void AttributionFields_SurviveTheJsonRoundTrip()
    {
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include,
        };

        var outer = Row(SkipReason.IndeterminateStructLayout, "Outer", BindingItemKind.Type, TypeDecl("Outer"));
        var inner = Row(SkipReason.AncestorSkipped, "Inner", BindingItemKind.Type, TypeDecl("Inner", "Outer"));
        SkipAttributionLinker.Link(new[] { outer, inner });

        var json = JsonConvert.SerializeObject(new[] { outer, inner }, settings);
        var restored = JsonConvert.DeserializeObject<List<SkippedItem>>(json, settings)!;

        Assert.Equal(inner.RootCauseId, restored[1].RootCauseId);
        Assert.Equal(inner.CascadeFrom, restored[1].CascadeFrom);
        Assert.Equal(inner.CauseOwner, restored[1].CauseOwner);
        Assert.Equal(inner.RecoveryStage, restored[1].RecoveryStage);
        Assert.Equal(inner.Confidence, restored[1].Confidence);
        // Enums serialize by name, so a reordering of either enum cannot silently re-map a stored row.
        var owner = Assert.NotNull(inner.CauseOwner);
        Assert.Contains(owner.ToString(), json);
    }

    /// <summary>
    /// Attribution cannot be computed before the whole pipeline has run, and the artifact manifest is
    /// written first. So an unlinked row must read as "not computed" rather than asserting an owner
    /// and a stage nobody determined.
    /// </summary>
    [Fact]
    public void AttributionFields_AreNullUntilLinkingRuns()
    {
        var item = Row(SkipReason.UnsupportedClosure, "foo");

        Assert.Null(item.CauseOwner);
        Assert.Null(item.RecoveryStage);
        Assert.Null(item.Confidence);
        Assert.Null(item.RootCauseId);
        Assert.Null(item.CascadeFrom);

        SkipAttributionLinker.Link(new[] { item });
        Assert.NotNull(item.CauseOwner);
    }

    /// <summary>
    /// A wrapper verify-recover withdrawal is decided by the Swift wrapper compile, not by emission —
    /// the emitter lowered the declaration fine; it was withdrawn because the compiled wrapper failed.
    /// The row shares <see cref="SkipReason.EmitterFault"/> with live emitter exceptions, so the stage
    /// must be refined from the withdrawal wording that <see cref="EmitterFaultRecord.Details"/> stamps.
    /// A report that places the withdrawal at Emit points a triager at the wrong pipeline stage.
    /// </summary>
    [Fact]
    public void Link_PlacesARecoveryWithdrawalRowAtSwiftCompile()
    {
        var decl = DeclId.Create("M", "T", BindingItemKind.Method, "foo");
        var withdrawal = EmitterFaultRecord.ForRecoveryWithdrawal(
            decl, RecoveryScope.LeafApi, "withdrawn to recover the wrapper compile (M.T.foo (leaf-api))");
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Method,
            Name = "foo",
            Reason = SkipReason.EmitterFault,
            Details = withdrawal.Details,
            DeclId = decl.Canonical,
        };

        SkipAttributionLinker.Link(new[] { item });

        Assert.Equal(RecoveryStage.SwiftCompile, item.RecoveryStage);
        Assert.Equal(CauseOwner.Generator, item.CauseOwner);
        Assert.Equal(AttributionConfidence.High, item.Confidence);
    }

    /// <summary>
    /// A live emitter exception keeps the Emit stage — the refinement must key on the withdrawal
    /// wording, not on the reason alone.
    /// </summary>
    [Fact]
    public void Link_KeepsAThrownEmitterFaultRowAtEmit()
    {
        var decl = DeclId.Create("M", "T", BindingItemKind.Method, "bar");
        var fault = EmitterFaultRecord.From(
            decl, RecoveryScope.LeafApi, new InvalidOperationException("boom"));
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Method,
            Name = "bar",
            Reason = SkipReason.EmitterFault,
            Details = fault.Details,
            DeclId = decl.Canonical,
        };

        SkipAttributionLinker.Link(new[] { item });

        Assert.Equal(RecoveryStage.Emit, item.RecoveryStage);
    }

    /// <summary>
    /// A property row names its accessor group, so a getter row and a setter row on one property
    /// resolve to the same unit rather than two.
    /// </summary>
    [Fact]
    public void RootCauseId_NamesTheAccessorGroupForAProperty()
    {
        var getter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Getter);
        var setter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Setter);
        var getterRow = Row(SkipReason.AsyncProperty, "value", BindingItemKind.Property, getter);
        var setterRow = Row(SkipReason.AsyncProperty, "value", BindingItemKind.Property, setter);

        SkipAttributionLinker.Link(new[] { getterRow, setterRow });

        Assert.Equal(getterRow.RootCauseId, setterRow.RootCauseId);
    }
}
