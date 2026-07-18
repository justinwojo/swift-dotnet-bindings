// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The recovery unit model: scope ordering, unit identity, artifact classification, and the graph
/// invariants the builder refuses to violate.
/// </summary>
public class RecoveryModelTests
{
    private static DeclId Type(string name, string declPath = "") =>
        DeclId.Create("M", declPath, BindingItemKind.Type, name);

    private static DeclId Method(string name, string declPath = "T") =>
        DeclId.Create("M", declPath, BindingItemKind.Method, name);

    private static DeclId Module() =>
        DeclId.Create("M", string.Empty, BindingItemKind.Module, "M");

    // ── Scope ordering ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_IsDefinedForEveryScope()
    {
        foreach (RecoveryScope scope in Enum.GetValues<RecoveryScope>())
            RecoveryScopeLattice.Rank(scope);
    }

    [Fact]
    public void Module_IsTheUniqueCoarsestScope()
    {
        var moduleRank = RecoveryScopeLattice.Rank(RecoveryScope.Module);
        foreach (RecoveryScope scope in Enum.GetValues<RecoveryScope>())
        {
            if (scope == RecoveryScope.Module)
                continue;
            Assert.True(RecoveryScopeLattice.Rank(scope) < moduleRank);
        }

        Assert.Equal(RecoveryScope.Module, RecoveryScopeLattice.Terminus);
    }

    [Theory]
    [InlineData(RecoveryScope.LeafApi, RecoveryScope.TypeSurface, true)]
    [InlineData(RecoveryScope.AccessorGroup, RecoveryScope.Module, true)]
    [InlineData(RecoveryScope.TypeRepresentation, RecoveryScope.TypeSurface, true)]
    [InlineData(RecoveryScope.TypeSurface, RecoveryScope.LeafApi, false)]
    [InlineData(RecoveryScope.LeafApi, RecoveryScope.AccessorGroup, false)]
    [InlineData(RecoveryScope.ForwardProtocolView, RecoveryScope.ManagedProtocolConformance, false)]
    public void CanEscalateTo_RequiresACoarserParent(RecoveryScope child, RecoveryScope parent, bool expected)
    {
        Assert.Equal(expected, RecoveryScopeLattice.CanEscalateTo(child, parent));
    }

    /// <summary>
    /// A nested type's coarsest meaningful blame is its containing type, not the module. Rank-strict
    /// escalation would outlaw that edge and force every nested-type failure to implicate the whole
    /// binding.
    /// </summary>
    [Fact]
    public void CanEscalateTo_PermitsTypeSurfaceIntoTypeSurface()
    {
        Assert.True(RecoveryScopeLattice.CanEscalateTo(RecoveryScope.TypeSurface, RecoveryScope.TypeSurface));
        Assert.True(RecoveryScopeLattice.PermitsSameScopeNesting(RecoveryScope.TypeSurface));
    }

    [Fact]
    public void CanEscalateTo_RejectsSameScopeForEveryOtherScope()
    {
        foreach (RecoveryScope scope in Enum.GetValues<RecoveryScope>())
        {
            if (scope == RecoveryScope.TypeSurface)
                continue;
            Assert.False(RecoveryScopeLattice.CanEscalateTo(scope, scope));
        }
    }

    [Fact]
    public void ScopeTokens_AreDistinctAndRoundTrip()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RecoveryScope scope in Enum.GetValues<RecoveryScope>())
        {
            var token = RecoveryScopeLattice.ToToken(scope);
            Assert.True(seen.Add(token), $"Duplicate scope token '{token}'.");
            Assert.DoesNotContain('!', token);
            Assert.True(RecoveryScopeLattice.TryParseToken(token, out var parsed));
            Assert.Equal(scope, parsed);
        }
    }

    // ── Unit identity ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnitId_RoundTripsThroughCanonical()
    {
        foreach (RecoveryScope scope in Enum.GetValues<RecoveryScope>())
        {
            var id = RecoveryUnitId.Create(Method("foo"), scope);
            Assert.True(RecoveryUnitId.TryParse(id.Canonical, out var parsed));
            Assert.Equal(id, parsed);
        }
    }

    /// <summary>
    /// A Swift declaration may legitimately be spelled with <c>!</c>. The split is on the last one, so
    /// a bang inside the declaration cannot be mistaken for the scope separator.
    /// </summary>
    [Fact]
    public void UnitId_RoundTripsWhenTheDeclarationContainsABang()
    {
        var id = RecoveryUnitId.Create(Method("init!"), RecoveryScope.LeafApi);
        Assert.Equal(id, RecoveryUnitId.Parse(id.Canonical));
    }

    [Fact]
    public void UnitId_SeparatesScopesOnOneDeclaration()
    {
        var decl = Type("P");
        Assert.NotEqual(
            RecoveryUnitId.Create(decl, RecoveryScope.ForwardProtocolView),
            RecoveryUnitId.Create(decl, RecoveryScope.ManagedProtocolConformance));
    }

    /// <summary>
    /// Getter and setter carry accessor-specific declaration ids on purpose. The accessor <em>group</em>
    /// is one unit, so both must name it — otherwise "the group" is two groups.
    /// </summary>
    [Fact]
    public void ForAccessorGroup_NormalizesGetterAndSetterOntoOneUnit()
    {
        var getter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Getter);
        var setter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Setter);

        Assert.NotEqual(getter, setter);
        Assert.Equal(
            RecoveryUnitId.ForAccessorGroup(getter),
            RecoveryUnitId.ForAccessorGroup(setter));
    }

    [Fact]
    public void ForSharedHelper_SeparatesBundlesOnOneModule()
    {
        var module = Module();
        Assert.NotEqual(
            RecoveryUnitId.ForSharedHelper(module, "utf8"),
            RecoveryUnitId.ForSharedHelper(module, "error-registry"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForSharedHelper_RejectsABlankBundleKey(string? bundleKey)
    {
        Assert.Throws<ArgumentException>(() => RecoveryUnitId.ForSharedHelper(Module(), bundleKey!));
    }

    [Fact]
    public void ForConformanceEdge_SeparatesEdgesOnOneConformer()
    {
        var conformer = Type("C");
        Assert.NotEqual(
            RecoveryUnitId.ForConformanceEdge(conformer, Type("P1")),
            RecoveryUnitId.ForConformanceEdge(conformer, Type("P2")));
    }

    /// <summary>
    /// <see cref="DeclId"/> is a record struct, so a default value carries a null path despite the
    /// non-nullable annotation. Asking whether it is enclosed must answer, not throw.
    /// </summary>
    [Fact]
    public void Encloses_ToleratesADefaultDeclId()
    {
        Assert.False(Type("Outer").Encloses(default));
        Assert.False(default(DeclId).Encloses(Type("Inner", "Outer")));
        Assert.False(default(DeclId).Encloses(default));
    }

    /// <summary>
    /// A conformance-edge qualifier embeds a whole <see cref="DeclId"/> canonical inside another one's
    /// discriminator, so the escaping has to survive one level of nesting. The bang and the pipe are
    /// both structural separators, which is what makes this the shape worth pinning.
    /// </summary>
    [Fact]
    public void QualifiedUnitIds_RoundTripThroughCanonical()
    {
        var ids = new[]
        {
            RecoveryUnitId.ForSharedHelper(Module(), "utf8!slice|v2"),
            RecoveryUnitId.ForConformanceEdge(Type("C"), Type("P")),
            RecoveryUnitId.ForConformanceEdge(Type("C"), Method("init!")),
        };

        foreach (var id in ids)
        {
            Assert.True(RecoveryUnitId.TryParse(id.Canonical, out var parsed), id.Canonical);
            Assert.Equal(id, parsed);
            Assert.Equal(id.Scope, parsed.Scope);
            Assert.Equal(id.Decl, parsed.Decl);
        }
    }

    /// <summary>
    /// The discriminator already separates declarations the structural fields cannot — an instance and
    /// a static property of one name. A qualifier must append, or it re-merges them.
    /// </summary>
    [Fact]
    public void Qualifiers_PreserveAnExistingDiscriminator()
    {
        var instance = DeclId.Create("M", "", BindingItemKind.Type, "T", discriminator: "instance");
        var stat = DeclId.Create("M", "", BindingItemKind.Type, "T", discriminator: "static");

        Assert.NotEqual(
            RecoveryUnitId.ForSharedHelper(instance, "utf8"),
            RecoveryUnitId.ForSharedHelper(stat, "utf8"));
    }

    [Fact]
    public void Encloses_IsTrueForNestingAndStrict()
    {
        var outer = Type("Outer");
        var inner = Type("Inner", declPath: "Outer");
        var deep = Type("Deepest", declPath: "Outer.Inner");
        var unrelated = Type("Other");

        Assert.True(outer.Encloses(inner));
        Assert.True(outer.Encloses(deep));
        Assert.True(RecoveryUnitId.Create(inner, RecoveryScope.TypeSurface).Decl.Encloses(deep));
        Assert.False(outer.Encloses(outer));
        Assert.False(inner.Encloses(outer));
        Assert.False(outer.Encloses(unrelated));
    }

    [Fact]
    public void Encloses_IsFalseAcrossModules()
    {
        var outer = DeclId.Create("A", "", BindingItemKind.Type, "Outer");
        var inner = DeclId.Create("B", "Outer", BindingItemKind.Type, "Inner");
        Assert.False(outer.Encloses(inner));
    }

    // ── Artifact classification ───────────────────────────────────────────────────────────────

    [Fact]
    public void EveryArtifactKind_HasAnExplicitRule()
    {
        foreach (RecoveryArtifactKind kind in Enum.GetValues<RecoveryArtifactKind>())
            Assert.Contains(kind, RecoveryUnitClassifier.ExplicitlyClassifiedKinds);
    }

    /// <summary>
    /// The bytes of a type can never leave while the type stays. This is the canonical
    /// compile-clean/ABI-corrupt outcome, so it is asserted directly rather than inferred.
    /// </summary>
    [Theory]
    [InlineData(RecoveryArtifactKind.StoredFieldCell)]
    [InlineData(RecoveryArtifactKind.EnumPayloadCell)]
    [InlineData(RecoveryArtifactKind.BufferSizeContributor)]
    public void RepresentationKinds_AreNeverDroppableAlone(RecoveryArtifactKind kind)
    {
        var rule = RecoveryUnitClassifier.Classify(kind);
        Assert.Equal(RecoveryScope.TypeRepresentation, rule.Scope);
        Assert.True(rule.ContributesToParentLayout);
        Assert.False(rule.DroppableAlone);
    }

    /// <summary>
    /// A managed reverse conformance owns every slot it counts, so withdrawing the whole capability
    /// shifts nothing. It carries a vtable footprint and is still droppable — which is exactly why
    /// layout contribution cannot be derived from footprint bits.
    /// </summary>
    [Fact]
    public void ManagedConformance_OwnsItsSlotsAndIsDroppableAsAWhole()
    {
        var rule = RecoveryUnitClassifier.Classify(RecoveryArtifactKind.ReverseVtable);
        Assert.Equal(RecoveryScope.ManagedProtocolConformance, rule.Scope);
        Assert.True(rule.Footprint.HasFlag(AbiFootprint.VtableSlot));
        Assert.True(rule.DroppableAlone);
    }

    /// <summary>
    /// Omitting a requirement from the forward view leaves Swift's own witness table untouched, so a
    /// forward member must not claim a vtable footprint.
    /// </summary>
    [Fact]
    public void ForwardInterfaceMember_DoesNotClaimAVtableSlot()
    {
        var rule = RecoveryUnitClassifier.Classify(RecoveryArtifactKind.ForwardInterfaceMember);
        Assert.False(rule.Footprint.HasFlag(AbiFootprint.VtableSlot));
        Assert.True(rule.DroppableAlone);
    }

    [Fact]
    public void UnmappedKind_ClassifiesConservatively()
    {
        var rule = RecoveryUnitClassifier.Classify((RecoveryArtifactKind)9999);
        Assert.False(rule.IsDeclared);
        Assert.True(rule.ContributesToParentLayout);
        Assert.False(rule.DroppableAlone);
        Assert.True(rule.Footprint.HasFlag(AbiFootprint.Unknown));
    }

    /// <summary>
    /// <c>Unclassified</c> is a declared sink, not an unmapped kind. Both are conservative, but only
    /// the second means "nobody has looked at this yet".
    /// </summary>
    [Fact]
    public void UnclassifiedSink_IsDeclaredButStillConservative()
    {
        var rule = RecoveryUnitClassifier.Classify(RecoveryArtifactKind.Unclassified);
        Assert.True(rule.IsDeclared);
        Assert.False(rule.DroppableAlone);
    }

    /// <summary>
    /// The role bridge is a coarse fallback for producers holding only an <see cref="ArtifactId"/>. It
    /// may never be less conservative than the truth, so nothing it yields is a representation kind —
    /// those must be stated explicitly by the producer that knows.
    /// </summary>
    [Fact]
    public void RoleBridge_NeverYieldsARepresentationKind()
    {
        foreach (ArtifactRole role in Enum.GetValues<ArtifactRole>())
        {
            foreach (BindingItemKind declKind in Enum.GetValues<BindingItemKind>())
            {
                if (!RecoveryUnitClassifier.TryFromArtifact(role, declKind, out var kind))
                    continue;
                Assert.NotEqual(RecoveryScope.TypeRepresentation, RecoveryUnitClassifier.ScopeOf(kind));
            }
        }
    }

    /// <summary>
    /// The four artifacts of one method are a single needs-closure bundle: the public member, its
    /// P/Invoke, the wrapper, and its callback thunks withdraw together or not at all.
    /// </summary>
    [Fact]
    public void RoleBridge_CollapsesOneMethodsArtifactsOntoOneKind()
    {
        var roles = new[]
        {
            ArtifactRole.CSharpPublic, ArtifactRole.PInvoke, ArtifactRole.SwiftWrapper, ArtifactRole.Callback,
        };

        var kinds = roles
            .Select(r => RecoveryUnitClassifier.FromArtifact(r, BindingItemKind.Method))
            .Distinct()
            .ToList();

        Assert.Single(kinds);
    }

    // ── Graph construction ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_RequiresAModuleUnit()
    {
        var builder = new RecoveryGraphBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_RejectsAnUndeclaredEscalationParent()
    {
        var builder = new RecoveryGraphBuilder();
        builder.DeclareModule(Module());
        builder.DeclareUnit(Method("foo"), RecoveryScope.LeafApi, RecoveryUnitId.Create(Type("Ghost"), RecoveryScope.TypeSurface));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void DeclareUnit_RejectsAParentThatIsNotCoarser()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, module);

        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, leaf));
    }

    /// <summary>
    /// Same-scope escalation is legal only into an enclosing declaration; two peer types naming each
    /// other is how the walk would stop terminating.
    /// </summary>
    [Fact]
    public void DeclareUnit_RejectsSameScopeEscalationBetweenPeers()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var peer = builder.DeclareUnit(Type("A"), RecoveryScope.TypeSurface, module);

        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Type("B"), RecoveryScope.TypeSurface, peer));
    }

    [Fact]
    public void DeclareUnit_AcceptsNestedTypeEscalatingIntoItsContainingType()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var outer = builder.DeclareUnit(Type("Outer"), RecoveryScope.TypeSurface, module);
        var inner = builder.DeclareUnit(Type("Inner", "Outer"), RecoveryScope.TypeSurface, outer);

        var graph = builder.Build();
        Assert.Equal(new[] { outer, module }, graph.Ancestors(inner).ToArray());
    }

    [Fact]
    public void DeclareUnit_RejectsAContradictoryRedeclaration()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        builder.DeclareUnit(Method("foo"), RecoveryScope.LeafApi, type);

        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Method("foo"), RecoveryScope.LeafApi, module));
    }

    [Fact]
    public void AddArtifact_RejectsASecondOwner()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var b = builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, type);

        var artifact = Method("a").Artifact(ArtifactRole.CSharpPublic);
        builder.AddArtifact(a, artifact, RecoveryArtifactKind.Method);

        Assert.Throws<ArgumentException>(() =>
            builder.AddArtifact(b, artifact, RecoveryArtifactKind.Method));
    }

    [Fact]
    public void AddArtifact_RejectsAKindFromADifferentScope()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);

        Assert.Throws<ArgumentException>(() =>
            builder.AddArtifact(type, Method("a").Artifact(ArtifactRole.CSharpPublic), RecoveryArtifactKind.Method));
    }

    /// <summary>
    /// A caller may state a layout contribution the kind table cannot see — a reverse slot position,
    /// which only <c>VtableLayout</c> knows — but marking is one-way, so nothing can be argued out of
    /// a contribution it really has.
    /// </summary>
    [Fact]
    public void ContributesToParentLayout_CanBeStatedByTheCallerAndIsMonotone()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type, contributesToParentLayout: true);

        // Re-declaring without the flag must not clear it.
        builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);

        var graph = builder.Build();
        Assert.True(graph.Find(leaf)!.ContributesToParentLayout);
        Assert.False(graph.Find(leaf)!.DroppableAlone);
    }

    [Fact]
    public void AddRequires_RejectsSelfAndUndeclaredUnits()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);

        Assert.Throws<ArgumentException>(() => builder.AddRequires(type, type));
        Assert.Throws<ArgumentException>(() =>
            builder.AddRequires(type, RecoveryUnitId.Create(Type("Ghost"), RecoveryScope.TypeSurface)));
    }

    /// <summary>
    /// The id a caller looks a unit up with must be the id the builder declared it under. Splitting
    /// those two surfaces would make every lookup miss and fail closed to <c>UnknownUnit</c>, which
    /// reads as "unmodelled surface" rather than as the wiring bug it is.
    /// </summary>
    [Fact]
    public void DeclareUnit_ProducesTheSameIdAsTheIdentityFactories()
    {
        var getter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Getter);
        var setter = DeclId.Create("M", "T", BindingItemKind.Property, "value", accessor: AccessorKind.Setter);

        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);

        var fromGetter = builder.DeclareUnit(getter, RecoveryScope.AccessorGroup, type);
        var fromSetter = builder.DeclareUnit(setter, RecoveryScope.AccessorGroup, type);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var edge = builder.DeclareConformanceEdge(Type("T"), Type("P"), module);

        // One group, declared from either accessor.
        Assert.Equal(fromGetter, fromSetter);
        Assert.Equal(RecoveryUnitId.ForAccessorGroup(getter), fromGetter);
        Assert.Equal(RecoveryUnitId.ForSharedHelper(Module(), "utf8"), helper);
        Assert.Equal(RecoveryUnitId.ForConformanceEdge(Type("T"), Type("P")), edge);

        var graph = builder.Build();
        Assert.True(graph.Contains(RecoveryUnitId.ForAccessorGroup(setter)));
        Assert.True(graph.Contains(RecoveryUnitId.ForSharedHelper(Module(), "utf8")));
        Assert.True(graph.Contains(RecoveryUnitId.ForConformanceEdge(Type("T"), Type("P"))));
    }

    /// <summary>
    /// Two scopes carry a key the generic overload has no way to supply. Accepting them there would
    /// silently collapse every helper bundle in a module — and every conformance of one type — onto a
    /// single unit, so the overload refuses rather than guessing.
    /// </summary>
    [Fact]
    public void DeclareUnit_RejectsScopesThatNeedADedicatedDeclarer()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());

        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Module(), RecoveryScope.SharedHelperBundle, module));
        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Type("T"), RecoveryScope.ConformanceEdge, module));
        Assert.Throws<ArgumentException>(() =>
            builder.DeclareUnit(Module(), RecoveryScope.Module, module));
    }

    [Fact]
    public void Provides_IsTheInverseOfRequires()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        builder.AddRequires(leaf, helper);

        var graph = builder.Build();
        Assert.Equal(new[] { helper }, graph.Requires(leaf).ToArray());
        Assert.Equal(new[] { leaf }, graph.Provides(helper).ToArray());
        Assert.Empty(graph.Provides(leaf));
    }

    [Fact]
    public void DependentClosure_IsTransitiveAndSurvivesMutualRequirement()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var b = builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, type);
        var c = builder.DeclareUnit(Method("c"), RecoveryScope.LeafApi, type);

        // b requires a, c requires b, and a requires c: mutual requirement is meaningful (they go
        // together), so the walk must absorb the cycle rather than spin on it.
        builder.AddRequires(b, a);
        builder.AddRequires(c, b);
        builder.AddRequires(a, c);

        var graph = builder.Build();
        var closure = graph.DependentClosure(new[] { a });
        Assert.Equal(3, closure.Count);
    }

    [Fact]
    public void DependentClosure_PreservesAnUnknownSeed()
    {
        var builder = new RecoveryGraphBuilder();
        builder.DeclareModule(Module());
        var graph = builder.Build();

        var ghost = RecoveryUnitId.Create(Type("Ghost"), RecoveryScope.TypeSurface);
        Assert.Contains(ghost, graph.DependentClosure(new[] { ghost }));
    }
}
