// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The protocol lane of the overload-collision resolver. A Swift delegate protocol routinely declares
/// requirements that share a base name and identical parameter TYPES and differ only by argument label
/// (<c>captureSession(_:didAdd:)</c> / <c>(_:didChange:)</c>); once labels are erased they are one C#
/// overload, so all but one used to be dropped as a <c>DuplicateSignature</c> — the consumer could see
/// the event fire but not which one. <see cref="ProtocolMethodDisambiguator"/> is the single per-protocol
/// map that lets every emission and dedup walk agree on which requirements survive and under what name.
///
/// <para>These tests pin the three properties the map is load-bearing for. First, WHICH groups it
/// touches and on which rung: the labels when they separate a family, otherwise the Swift parameter
/// types — the same ladder in the same per-family order as the class lane, because an interface member
/// the conforming class names differently costs the whole conformance. Second, that the names it hands
/// out come from each requirement's own content —
/// its labels, else its Swift parameter types, else nothing at all — never a positional number, because
/// an interface member name propagates to every conformer and proxy in the consumer's code. Third, that
/// the same map answers all four <c>Effective*</c> axes, so the interface, proxy, receiver and validator
/// walks cannot disagree about a member.</para>
///
/// <para>The map memoizes per <see cref="ProtocolDecl"/> by reference identity, so every test builds its
/// own protocol instance rather than sharing one.</para>
/// </summary>
public class ProtocolMethodDisambiguatorTests
{
    // ===================================================================
    //  Fixtures
    // ===================================================================

    /// <summary>An instance requirement: <c>name(label1: T1, label2: T2)</c>. A <c>"_"</c> label is positional.</summary>
    private static MethodDecl Requirement(string name, params (string label, string type)[] args)
        => TestDecls.Method(name, parameters: args.Select(a => TestDecls.Param(a.label, new NamedTypeSpec(a.type))));

    private static MethodDecl IntRequirement(string name, params string[] labels)
        => Requirement(name, labels.Select(l => (l, "Swift.Int")).ToArray());

    /// <summary>Swift.Int and Swift.String registered — enough for every projected key these tests build.</summary>
    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static string? NameFor(IReadOnlyDictionary<string, string> map, MethodDecl m)
        => map.TryGetValue(EveryProtocolEmitter.GetMethodKey(m), out var name) ? name : null;

    /// <summary>
    /// A conforming type carrying the same members, so one Swift shape can be run through BOTH lanes. The
    /// members are re-built rather than shared with the protocol: <c>TestDecls.Protocol</c> promotes what it
    /// is handed to a requirement, and a decl cannot be a requirement and a class member at once.
    /// </summary>
    private static StructDecl ConformingType(string name, params MethodDecl[] members)
    {
        var typeDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = members.ToList(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
        foreach (var m in members)
            m.ParentDecl = typeDecl;
        return typeDecl;
    }

    /// <summary>The base name a lane hands a member: the class lane's assignment, or its own Swift name.</summary>
    private static string ClassLaneNameInput(IReadOnlyDictionary<MethodDecl, OverloadNameAssignment> map, MethodDecl m)
        => map.TryGetValue(m, out var a) && a.NameInput != null ? a.NameInput : m.Name;

    // ===================================================================
    //  Which groups get disambiguated
    // ===================================================================

    [Fact]
    public void Compute_LabelOnlyCollision_NamesEachRequirementFromItsOwnLabels()
    {
        var db = CreateTypeDatabase();
        var activate = IntRequirement("conversationManager", "_", "didActivate");
        var deactivate = IntRequirement("conversationManager", "_", "didDeactivate");
        var protocolDecl = TestDecls.Protocol("ConversationManagerDelegate", activate, deactivate);

        // Precondition: this pair really is one C# overload once labels are erased — otherwise the test
        // would be asserting against a group that never collided.
        Assert.Equal(
            ProtocolSignatureHelper.GetProjectedCSharpMethodKey(activate, db, protocolDecl, propertyNames: null),
            ProtocolSignatureHelper.GetProjectedCSharpMethodKey(deactivate, db, protocolDecl, propertyNames: null));

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal("conversationManagerDidActivate", NameFor(map, activate));
        Assert.Equal("conversationManagerDidDeactivate", NameFor(map, deactivate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_LabelOnlyCollision_NamesDoNotDependOnDeclarationOrder(bool reversed)
    {
        var db = CreateTypeDatabase();
        var add = IntRequirement("captureSession", "_", "didAdd");
        var change = IntRequirement("captureSession", "_", "didChange");
        var protocolDecl = reversed
            ? TestDecls.Protocol("CaptureSessionObserver", change, add)
            : TestDecls.Protocol("CaptureSessionObserver", add, change);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        // An interface member name is the one a conformer types into its own source, so a later upstream
        // requirement landing above these two must not re-letter them.
        Assert.Equal("captureSessionDidAdd", NameFor(map, add));
        Assert.Equal("captureSessionDidChange", NameFor(map, change));
    }

    [Fact]
    public void Compute_TypeErasureCollision_FallsToTheTypeRung()
    {
        var db = CreateTypeDatabase();
        // Two unresolvable parameter types both project to AnyType, so the requirements collide — and
        // their labels are identical, so no label-derived name can tell them apart. The family drops to
        // the type rung rather than collapsing, because a conforming Swift class resolves this same shape
        // on ITS type rung: if the interface kept a bare `Add` here, the class would emit only
        // `AddWithExpression`/`AddWithSendable` and the conformance would be dropped as unsatisfiable.
        var byExpression = Requirement("add", ("_", "UnknownModule.Expression"));
        var bySendable = Requirement("add", ("_", "UnknownModule.Sendable"));
        var protocolDecl = TestDecls.Protocol("Container", byExpression, bySendable);

        Assert.Equal(
            ProtocolSignatureHelper.GetProjectedCSharpMethodKey(byExpression, db, protocolDecl, propertyNames: null),
            ProtocolSignatureHelper.GetProjectedCSharpMethodKey(bySendable, db, protocolDecl, propertyNames: null));

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal(2, map.Count);
        Assert.Equal("addWithExpression", ProtocolMethodDisambiguator.EffectiveNameInput(byExpression, protocolDecl, db));
        Assert.Equal("addWithSendable", ProtocolMethodDisambiguator.EffectiveNameInput(bySendable, protocolDecl, db));
    }

    /// <summary>
    /// The cross-lane pin for a pure type-erasure family. <see cref="ProtocolConformanceValidator"/> keeps a
    /// conformance only when the interface's member name equals the name the conforming class body emits, so
    /// the two lanes must land on the same string for the same Swift shape — and they must reach it the same
    /// way, from each member's own label-derived input fed through the shared type-token builder.
    ///
    /// <para>The expected literals here are the ones
    /// <c>OverloadNameDisambiguatorTests.Resolve_TwoLabellessMembers_NeitherOwnsTheBareName</c> pins on the
    /// class lane for the identical Swift shape. If either lane's ladder drifts, exactly one of the two
    /// tests goes red — which is the point: agreement is what is under test, not either name alone.</para>
    /// </summary>
    [Fact]
    public void Compute_TypeErasureCollision_NamesMatchTheClassLaneForTheSameShape()
    {
        var db = CreateTypeDatabase();
        var plain = Requirement("transform", ("_", "TestModule.RefBox"));
        var optional = Requirement("transform", ("_", "Swift.Optional"));
        var protocolDecl = TestDecls.Protocol("Transformer", plain, optional);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal(2, map.Count);
        Assert.Equal("transformWithRefBox", ProtocolMethodDisambiguator.EffectiveNameInput(plain, protocolDecl, db));
        Assert.Equal("transformWithOptional", ProtocolMethodDisambiguator.EffectiveNameInput(optional, protocolDecl, db));

        // Neither member owns the bare name — the same outcome the class lane reaches, and the reason the
        // conformance survives: the interface asks for nothing the class body does not declare.
        Assert.DoesNotContain("transform", map.Values);
    }

    /// <summary>
    /// Ownership is settled BEFORE a rung is chosen, so a third sibling's duplicate label cannot drag the
    /// label-less member off the bare name. The class lane awards the owner first and only then rung-selects
    /// what remains; deciding the rung first would make the interface require <c>TransformWithRefBox</c>
    /// while the conforming class still declares <c>Transform</c>, and the conformance is dropped.
    /// </summary>
    [Fact]
    public void Compute_LabelDuplicateAmongSiblings_DoesNotDragTheBareNameOwnerToTheTypeRung()
    {
        var db = CreateTypeDatabase();
        var labelless = Requirement("transform", ("_", "TestModule.RefBox"));
        var byBoxed = Requirement("transform", ("bar", "TestModule.RefBox"));
        var byOptional = Requirement("transform", ("bar", "Swift.Optional"));
        var protocolDecl = TestDecls.Protocol("Transformer", labelless, byBoxed, byOptional);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        // The label-less member keeps the family's natural name — it is absent from the map, which is what
        // "keeps its own name" means here. Only the two that genuinely need discriminating take the rung.
        Assert.Equal(2, map.Count);
        Assert.Equal("transform", ProtocolMethodDisambiguator.EffectiveNameInput(labelless, protocolDecl, db));
        Assert.Equal("transformBarWithRefBox", ProtocolMethodDisambiguator.EffectiveNameInput(byBoxed, protocolDecl, db));
        Assert.Equal("transformBarWithOptional", ProtocolMethodDisambiguator.EffectiveNameInput(byOptional, protocolDecl, db));
    }

    /// <summary>
    /// A family already on the type rung has no rung left. Re-composing from its stored input would append
    /// the parameter types a second time — a spelling neither lane nor any conforming class would produce —
    /// so a blocked member is left out of the map to collapse instead.
    /// </summary>
    [Fact]
    public void Compute_TypeRungNameAlreadyTaken_DoesNotDoubleApplyTheTypeToken()
    {
        var db = CreateTypeDatabase();
        var plain = Requirement("transform", ("_", "TestModule.RefBox"));
        var optional = Requirement("transform", ("_", "Swift.Optional"));
        // An uncontested sibling whose NATURAL name is exactly the type-rung name the pair wants.
        var occupant = Requirement("transformWithRefBox", ("_", "TestModule.RefBox"));
        var protocolDecl = TestDecls.Protocol("Transformer", plain, optional, occupant);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.DoesNotContain(map.Values, v => v.Contains("WithRefBoxWith", StringComparison.Ordinal));
        Assert.DoesNotContain(map.Values, v => v.Contains("withRefBoxWith", StringComparison.Ordinal));

        // The positive half: the sibling whose type-rung name is free still gets it, the occupant keeps its
        // own name, and the blocked member is left out of the map to collapse through the emitted-signature
        // dedup. Without these, dropping the whole family would also satisfy the negative assertions above.
        Assert.Equal("transformWithOptional", ProtocolMethodDisambiguator.EffectiveNameInput(optional, protocolDecl, db));
        Assert.Equal("transformWithRefBox", ProtocolMethodDisambiguator.EffectiveNameInput(occupant, protocolDecl, db));
        Assert.DoesNotContain(map.Values, v => string.Equals(v, "transform", StringComparison.Ordinal));
    }

    [Fact]
    public void Compute_NoCollision_ReturnsAnEmptyMap()
    {
        var db = CreateTypeDatabase();
        var byInt = Requirement("handle", ("value", "Swift.Int"));
        var byString = Requirement("handle", ("value", "Swift.String"));
        var protocolDecl = TestDecls.Protocol("Handler", byInt, byString);

        // Distinct projected C# overloads never share a group, so the common case costs nothing and
        // every Effective* helper falls through to the pre-existing key/name.
        Assert.Empty(ProtocolMethodDisambiguator.Compute(protocolDecl, db));
    }

    [Fact]
    public void Compute_SingleRequirement_ReturnsAnEmptyMap()
    {
        var db = CreateTypeDatabase();
        var protocolDecl = TestDecls.Protocol("Solo", IntRequirement("configure", "mode"));

        Assert.Empty(ProtocolMethodDisambiguator.Compute(protocolDecl, db));
    }

    [Fact]
    public void Compute_TrueDuplicateRequirements_CollapseAsBefore()
    {
        var db = CreateTypeDatabase();
        // Same name, same labels, same types: one slot key for both. There is no second requirement to
        // preserve, so renaming would invent a member rather than rescue one.
        var protocolDecl = TestDecls.Protocol(
            "Dup",
            IntRequirement("reset", "to"),
            IntRequirement("reset", "to"));

        Assert.Empty(ProtocolMethodDisambiguator.Compute(protocolDecl, db));
    }

    [Fact]
    public void Compute_StaticAndInitRequirements_AreOutOfSlice()
    {
        var db = CreateTypeDatabase();
        // Statics and constructors take separate emission paths, so they must not be pulled into a
        // group with (or in place of) the instance requirements this map governs.
        var staticA = TestDecls.Method("make", MethodType.Static, parameters: new[] { TestDecls.Param("first", new NamedTypeSpec("Swift.Int")) });
        var staticB = TestDecls.Method("make", MethodType.Static, parameters: new[] { TestDecls.Param("second", new NamedTypeSpec("Swift.Int")) });
        var ctor = TestDecls.Method("init", isConstructor: true, parameters: new[] { TestDecls.Param("value", new NamedTypeSpec("Swift.Int")) });
        var protocolDecl = TestDecls.Protocol("Factory", staticA, staticB, ctor);

        Assert.Empty(ProtocolMethodDisambiguator.Compute(protocolDecl, db));
    }

    // ===================================================================
    //  Bare-name ownership
    // ===================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_SoleLabellessRequirement_KeepsTheBareName(bool labelledFirst)
    {
        var db = CreateTypeDatabase();
        var bare = IntRequirement("configure", "_");
        var labelled = IntRequirement("configure", "mode");
        var protocolDecl = labelledFirst
            ? TestDecls.Protocol("Configurable", labelled, bare)
            : TestDecls.Protocol("Configurable", bare, labelled);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        // The label-less requirement has nothing to be discriminated BY, so it owns the family's natural
        // name — absent from the map IS how it keeps it. A conforming class runs the same rule in its own
        // lane, so it declares the member under the name the interface asks for instead of having the
        // whole conformance dropped as unsatisfiable.
        Assert.Null(NameFor(map, bare));
        Assert.Equal("configure", ProtocolMethodDisambiguator.EffectiveNameInput(bare, protocolDecl, db));
        Assert.Equal("configureMode", NameFor(map, labelled));
    }

    [Fact]
    public void Compute_BareNameOwner_DoesNotBlockItsSiblingsProjectedKey()
    {
        var db = CreateTypeDatabase();
        var bare = IntRequirement("configure", "_");
        var labelled = IntRequirement("configure", "mode");
        var protocolDecl = TestDecls.Protocol("Configurable", bare, labelled);

        // The point of the split: the two requirements that shared one projected C# overload now hold
        // two, so both survive dedup instead of one being dropped.
        Assert.NotEqual(
            ProtocolMethodDisambiguator.EffectiveProjectedKey(bare, protocolDecl, db, propertyNames: null),
            ProtocolMethodDisambiguator.EffectiveProjectedKey(labelled, protocolDecl, db, propertyNames: null));
    }

    // ===================================================================
    //  Family fold — uniform naming across a mixed renamed/bare family
    // ===================================================================

    [Fact]
    public void Compute_MixedFamily_FoldsTheLabelsOfTheTypeDistinctSibling()
    {
        var db = CreateTypeDatabase();
        var didAdd = IntRequirement("room", "_", "didAdd");
        var didRemove = IntRequirement("room", "_", "didRemove");
        // Three parameters, so a DISTINCT C# overload that never entered a collision group — it would
        // otherwise read as a bare `Room(...)` beside two renamed siblings.
        var didFinish = IntRequirement("room", "_", "didFinishWith", "error");
        var protocolDecl = TestDecls.Protocol("RoomActivityObserver", didAdd, didRemove, didFinish);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal("roomDidAdd", NameFor(map, didAdd));
        Assert.Equal("roomDidRemove", NameFor(map, didRemove));
        Assert.Equal("roomDidFinishWithError", NameFor(map, didFinish));
    }

    [Fact]
    public void Compute_MixedFamilySiblingWithNoFoldableLabel_StaysBare()
    {
        var db = CreateTypeDatabase();
        var didAdd = IntRequirement("room", "_", "didAdd");
        var didRemove = IntRequirement("room", "_", "didRemove");
        // One positional argument: folding its labels would produce its own name back, so the honest
        // answer is to leave it alone rather than re-alias it onto its natural key.
        var single = IntRequirement("room", "_");
        var protocolDecl = TestDecls.Protocol("RoomActivityObserver", didAdd, didRemove, single);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal("roomDidAdd", NameFor(map, didAdd));
        Assert.Null(NameFor(map, single));
    }

    [Fact]
    public void Compute_UnrelatedBaseName_IsNotFolded()
    {
        var db = CreateTypeDatabase();
        var didAdd = IntRequirement("room", "_", "didAdd");
        var didRemove = IntRequirement("room", "_", "didRemove");
        // A different base name entirely — the fold is scoped to the family that already has a renamed
        // member, so an unrelated labelled requirement keeps its own name.
        var unrelated = IntRequirement("session", "_", "didStart");
        var protocolDecl = TestDecls.Protocol("RoomActivityObserver", didAdd, didRemove, unrelated);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Null(NameFor(map, unrelated));
        Assert.Equal("session", ProtocolMethodDisambiguator.EffectiveNameInput(unrelated, protocolDecl, db));
    }

    // ===================================================================
    //  The ladder — labels, then types, then nothing
    // ===================================================================

    [Fact]
    public void Compute_LabelNameTakenByAnUncontestedSibling_FallsToTheTypeRung()
    {
        var db = CreateTypeDatabase();
        var withMode = IntRequirement("configure", "_", "mode");
        var withOther = IntRequirement("configure", "_", "other");
        // A real, uncontested requirement already emits as `ConfigureMode(nint, nint)`.
        var existing = IntRequirement("configureMode", "_", "_");
        var protocolDecl = TestDecls.Protocol("Configurable", withMode, withOther, existing);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        // One discriminand's label-derived name is occupied, so the WHOLE family moves to the type rung —
        // including the sibling whose own label rung was free. Escalating only the blocked member is what
        // splits the lanes: a conforming class body resolves this same shape through the class lane, which
        // moves both members down together, so an interface that kept `ConfigureOther` here would ask for a
        // member the class never declares and the whole conformance would be dropped as unsatisfiable.
        Assert.Equal("configureModeWithIntAndInt", NameFor(map, withMode));
        Assert.Equal("configureOtherWithIntAndInt", NameFor(map, withOther));
        Assert.Null(NameFor(map, existing));
    }

    /// <summary>
    /// The discrimination control for the escalation above: with no occupied name in sight, the family stays
    /// on the label rung. Without this, "escalate the whole family" could degenerate into "always take the
    /// type rung", which would rename every delegate-callback pair in every binding for no reason.
    /// </summary>
    [Fact]
    public void Compute_NoReservedCollision_KeepsTheWholeFamilyOnTheLabelRung()
    {
        var db = CreateTypeDatabase();
        var withMode = IntRequirement("configure", "_", "mode");
        var withOther = IntRequirement("configure", "_", "other");
        // Same family, but the uncontested sibling's natural name is nowhere near either label-derived name.
        var unrelated = IntRequirement("reset", "_", "_");
        var protocolDecl = TestDecls.Protocol("Configurable", withMode, withOther, unrelated);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal("configureMode", NameFor(map, withMode));
        Assert.Equal("configureOther", NameFor(map, withOther));
        Assert.Null(NameFor(map, unrelated));
    }

    [Fact]
    public void Compute_NeitherRungFree_LeavesTheSlotOutOfTheMapRatherThanNumberingIt()
    {
        var db = CreateTypeDatabase();
        var withMode = IntRequirement("configure", "_", "mode");
        var withOther = IntRequirement("configure", "_", "other");
        // Uncontested siblings occupying BOTH rungs one of the two requirements could reach.
        var takesLabelRung = IntRequirement("configureMode", "_", "_");
        var takesTypeRung = IntRequirement("configureModeWithIntAndInt", "_", "_");
        var protocolDecl = TestDecls.Protocol("Configurable", withMode, withOther, takesLabelRung, takesTypeRung);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        // No rung frees the family as a unit, so each member takes its type-derived name if that key is
        // still free — the same first-fit a conforming class body applies, which is why the survivor carries
        // the same name in both lanes. The blocked one is left out of the map rather than numbered: a
        // `Configure2` would propagate a meaningless name to every conformer and proxy in the consumer's
        // code, so it keeps its natural key and collapses through the ordinary duplicate-signature dedup.
        Assert.Null(NameFor(map, withMode));
        Assert.Equal("configureOtherWithIntAndInt", NameFor(map, withOther));
        Assert.All(map.Values, name => Assert.False(char.IsDigit(name[^1]), $"'{name}' ends in a digit"));
    }

    /// <summary>
    /// The other half of the unseparable arm: here the family's members reach the SAME type-derived name
    /// (their Swift types share a simple name and differ only by module, and both erase to the same C#
    /// projection), so the first one through claims it and the second has nowhere left to go. The blocked
    /// member must land on the survivor's name, not on its own natural one — the class lane refuses it
    /// outright, and the nearest thing this map has to a refusal is a name that collides so the ordinary
    /// duplicate-signature dedup drops it. Leaving it under its natural name would publish a second
    /// interface member callable exactly like the survivor.
    /// </summary>
    [Fact]
    public void Compute_UnseparableFamily_AliasesTheBlockedMemberOntoTheSurvivorsName()
    {
        var db = CreateTypeDatabase();
        var first = Requirement("record", ("value", "TestModule.Collapse"));
        var second = Requirement("record", ("value", "OtherModule.Collapse"));
        var protocolDecl = TestDecls.Protocol("OverloadCollapse", first, second);

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal("recordValueWithCollapse", NameFor(map, first));
        Assert.Equal(NameFor(map, first), NameFor(map, second));
    }

    // ===================================================================
    //  Cross-lane parity — the invariant the two ladders exist to hold
    // ===================================================================

    /// <summary>
    /// The invariant asserted directly rather than through two lanes' worth of expected literals: for ONE
    /// Swift shape, the class-lane resolver and the protocol-lane map hand every member the same base name.
    /// That equality is what <see cref="ProtocolConformanceValidator"/> checks before it keeps a conformance,
    /// so a lane that names a member differently costs the consumer the entire <c>: IFoo</c>.
    ///
    /// <para>The shape is the one where the lanes are easiest to drift apart: a two-member family whose
    /// label-derived name for ONE member is already occupied by an uncontested sibling. Accepting names
    /// member-by-member keeps the unblocked sibling on the label rung in one lane while the other moves the
    /// whole family down — a divergence no expected-literal test on either lane alone would notice.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_ContestedFamily_NamesEveryMemberTheSameAsTheClassLane(bool reservedCollision)
    {
        var db = CreateTypeDatabase();

        // `configureMode(_:_:)` is present only in the collision arm; without it neither lane has a reason
        // to leave the label rung, which is what makes the two arms a discrimination pair.
        MethodDecl[] Members() => reservedCollision
            ? new[]
            {
                IntRequirement("configure", "_", "mode"),
                IntRequirement("configure", "_", "other"),
                IntRequirement("configureMode", "_", "_"),
            }
            : new[]
            {
                IntRequirement("configure", "_", "mode"),
                IntRequirement("configure", "_", "other"),
            };

        var requirements = Members();
        var protocolDecl = TestDecls.Protocol("Configurable", requirements);

        var witnesses = Members();
        var classMap = OverloadNameDisambiguator.ForTypeBody(ConformingType("Configurator", witnesses), db);

        for (int i = 0; i < requirements.Length; i++)
        {
            Assert.Equal(
                ClassLaneNameInput(classMap, witnesses[i]),
                ProtocolMethodDisambiguator.EffectiveNameInput(requirements[i], protocolDecl, db));
        }

        // Anchor what "the same" is, so the two lanes cannot agree by both doing nothing: the collision arm
        // moves the whole family to the type rung, the control arm keeps it on the labels.
        Assert.Equal(
            reservedCollision ? "configureOtherWithIntAndInt" : "configureOther",
            ProtocolMethodDisambiguator.EffectiveNameInput(requirements[1], protocolDecl, db));
    }

    [Fact]
    public void Compute_NoAssignedNameCarriesANumericSuffix()
    {
        var db = CreateTypeDatabase();
        var protocolDecl = TestDecls.Protocol(
            "Observer",
            IntRequirement("captureSession", "_", "didAdd"),
            IntRequirement("captureSession", "_", "didChange"),
            IntRequirement("captureSession", "_", "didUpdate"));

        var map = ProtocolMethodDisambiguator.Compute(protocolDecl, db);

        Assert.Equal(3, map.Count);
        Assert.All(map.Values, name => Assert.False(char.IsDigit(name[^1]), $"'{name}' ends in a digit"));
    }

    // ===================================================================
    //  Family walk order
    // ===================================================================

    [Fact]
    public void Compute_ResolvesFamiliesInDeclarationOrder()
    {
        var db = CreateTypeDatabase();
        var protocolDecl = TestDecls.Protocol(
            "Observer",
            IntRequirement("zeta", "_", "one"),
            IntRequirement("zeta", "_", "two"),
            IntRequirement("alpha", "_", "one"),
            IntRequirement("alpha", "_", "two"),
            IntRequirement("mid", "_", "one"),
            IntRequirement("mid", "_", "two"));

        ReportCollector.Start(TestModelFactory.CreateModuleDecl());
        ProtocolMethodDisambiguator.Compute(protocolDecl, db);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        // Families are resolved against a name reservation set that accumulates ACROSS families, so
        // which family is walked first decides which one gets to claim a contested name — the order is
        // public-surface-affecting rather than incidental, and the only order with a meaning behind it
        // is the order the requirements are declared in. Every assignment is recorded as the walk makes
        // it, so the recorded sequence IS the walk order.
        Assert.NotNull(report);
        Assert.Equal(6, report!.OverloadRenames.Count);
        var families = report.OverloadRenames
            .Select(r => r.SwiftSignature.Substring(0, r.SwiftSignature.IndexOf('(')))
            .Distinct()
            .ToList();
        Assert.Equal(new[] { "zeta", "alpha", "mid" }, families);
    }

    // ===================================================================
    //  One map, four axes
    // ===================================================================

    [Fact]
    public void EffectiveProjectedKey_DisambiguatedSiblings_AreDistinct()
    {
        var db = CreateTypeDatabase();
        var activate = IntRequirement("conversationManager", "_", "didActivate");
        var deactivate = IntRequirement("conversationManager", "_", "didDeactivate");
        var protocolDecl = TestDecls.Protocol("ConversationManagerDelegate", activate, deactivate);

        // The whole point: the projected keys the member dedup runs on must separate, or the second
        // requirement is dropped before it can be named.
        Assert.NotEqual(
            ProtocolMethodDisambiguator.EffectiveProjectedKey(activate, protocolDecl, db, propertyNames: null),
            ProtocolMethodDisambiguator.EffectiveProjectedKey(deactivate, protocolDecl, db, propertyNames: null));
    }

    [Fact]
    public void EffectiveRawKey_DisambiguatedSiblings_TakeTheirLabelInclusiveSlotKey()
    {
        var db = CreateTypeDatabase();
        var activate = IntRequirement("conversationManager", "_", "didActivate");
        var deactivate = IntRequirement("conversationManager", "_", "didDeactivate");
        var protocolDecl = TestDecls.Protocol("ConversationManagerDelegate", activate, deactivate);

        // The fillability axis has to see two requirements, so both fill their own vtable slot.
        Assert.Equal(
            EveryProtocolEmitter.GetMethodKey(activate),
            ProtocolMethodDisambiguator.EffectiveRawKey(activate, protocolDecl, db));
        Assert.NotEqual(
            ProtocolMethodDisambiguator.EffectiveRawKey(activate, protocolDecl, db),
            ProtocolMethodDisambiguator.EffectiveRawKey(deactivate, protocolDecl, db));
    }

    [Fact]
    public void EffectiveWitnessSlotKey_DisambiguatedSiblings_EarnSeparateForwardSlots()
    {
        var db = CreateTypeDatabase();
        var activate = IntRequirement("conversationManager", "_", "didActivate");
        var deactivate = IntRequirement("conversationManager", "_", "didDeactivate");
        var protocolDecl = TestDecls.Protocol("ConversationManagerDelegate", activate, deactivate);

        // Forward (SBW) dispatch is label-BLIND by default, which collapses this pair onto one slot —
        // correct only while they also collapse to one C# member. Now that both survive, a Swift-backed
        // proxy has to be able to forward each to its own witness.
        Assert.Equal(
            WitnessDispatchEmitter.GetMethodKey(activate),
            WitnessDispatchEmitter.GetMethodKey(deactivate));
        Assert.NotEqual(
            ProtocolMethodDisambiguator.EffectiveWitnessSlotKey(activate, protocolDecl, db),
            ProtocolMethodDisambiguator.EffectiveWitnessSlotKey(deactivate, protocolDecl, db));
    }

    [Fact]
    public void Effective_NonDisambiguatedMethod_FallsThroughToTheUnchangedKeys()
    {
        var db = CreateTypeDatabase();
        var byInt = Requirement("handle", ("value", "Swift.Int"));
        var byString = Requirement("handle", ("value", "Swift.String"));
        var protocolDecl = TestDecls.Protocol("Handler", byInt, byString);

        // A protocol with no label collision must produce byte-identical output to the pre-disambiguator
        // generator, which means every axis reads exactly the key it read before.
        Assert.Equal("handle", ProtocolMethodDisambiguator.EffectiveNameInput(byInt, protocolDecl, db));
        Assert.Equal(
            ProtocolSignatureHelper.GetMethodSignatureKey(byInt, db, protocolDecl),
            ProtocolMethodDisambiguator.EffectiveRawKey(byInt, protocolDecl, db));
        Assert.Equal(
            ProtocolSignatureHelper.GetProjectedCSharpMethodKey(byInt, db, protocolDecl, propertyNames: null),
            ProtocolMethodDisambiguator.EffectiveProjectedKey(byInt, protocolDecl, db, propertyNames: null));
        Assert.Equal(
            WitnessDispatchEmitter.GetMethodKey(byInt),
            ProtocolMethodDisambiguator.EffectiveWitnessSlotKey(byInt, protocolDecl, db));
    }

    [Fact]
    public void Effective_NullProtocol_IsANoOp()
    {
        var db = CreateTypeDatabase();
        var m = IntRequirement("configure", "mode");

        // Class-lane and free-function callers reach these helpers with no protocol in scope.
        Assert.Empty(ProtocolMethodDisambiguator.Compute(null, db));
        Assert.Equal("configure", ProtocolMethodDisambiguator.EffectiveNameInput(m, null, db));
    }

    // ===================================================================
    //  Memoization and the ship-gate ledger
    // ===================================================================

    [Fact]
    public void Compute_SameProtocolInstance_ReturnsTheSameMap()
    {
        var db = CreateTypeDatabase();
        var protocolDecl = TestDecls.Protocol(
            "ConversationManagerDelegate",
            IntRequirement("conversationManager", "_", "didActivate"),
            IntRequirement("conversationManager", "_", "didDeactivate"));

        // The emitter and the separately-invoked conformance validator both recompute this map. They
        // agree only because the recompute resolves the SAME instance — a per-call result could pick a
        // different name at each site and leave a conformer missing an interface member.
        Assert.Same(
            ProtocolMethodDisambiguator.Compute(protocolDecl, db),
            ProtocolMethodDisambiguator.Compute(protocolDecl, db));
    }

    [Fact]
    public void Compute_EqualButDistinctProtocols_DoNotShareAMap()
    {
        var db = CreateTypeDatabase();
        MethodDecl[] Requirements() => new[]
        {
            IntRequirement("conversationManager", "_", "didActivate"),
            IntRequirement("conversationManager", "_", "didDeactivate"),
        };

        // ProtocolDecl is a record, so value equality would alias two protocols that merely look alike.
        Assert.NotSame(
            ProtocolMethodDisambiguator.Compute(TestDecls.Protocol("Delegate", Requirements()), db),
            ProtocolMethodDisambiguator.Compute(TestDecls.Protocol("Delegate", Requirements()), db));
    }

    [Fact]
    public void Compute_RecordsEachAssignmentInTheOverloadRenameLedger()
    {
        var db = CreateTypeDatabase();
        var activate = IntRequirement("conversationManager", "_", "didActivate");
        var deactivate = IntRequirement("conversationManager", "_", "didDeactivate");
        var protocolDecl = TestDecls.Protocol("ConversationManagerDelegate", activate, deactivate);
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        ReportCollector.Start(moduleDecl);
        ProtocolMethodDisambiguator.Compute(protocolDecl, db);
        var report = ReportCollector.Complete();

        // An interface requirement is public surface too, so the ship gate — which reads these records
        // rather than scanning emitted identifiers — has to be able to see this lane's decisions.
        Assert.NotNull(report);
        var renames = report!.OverloadRenames.Where(r => r.NaturalName == "ConversationManager").ToList();
        Assert.Equal(2, renames.Count);
        Assert.All(renames, r =>
        {
            Assert.Equal(nameof(OverloadNameOutcome.LabelDerived), r.Scheme);
            // Both names are what makes the record auditable: a gate holding only the emitted name
            // cannot tell a resolver-assigned identifier from one the Swift author wrote.
            Assert.NotEqual(r.NaturalName, r.EmittedName);
            Assert.Contains("conversationManager(", r.SwiftSignature);
        });
        Assert.Contains(renames, r => r.EmittedName == "ConversationManagerDidActivate");
        Assert.Contains(renames, r => r.EmittedName == "ConversationManagerDidDeactivate");
    }

    [Fact]
    public void Compute_TypeRungFamily_IsLedgeredAsTypeDerived()
    {
        var db = CreateTypeDatabase();
        var plain = Requirement("transform", ("_", "TestModule.RefBox"));
        var optional = Requirement("transform", ("_", "Swift.Optional"));
        var protocolDecl = TestDecls.Protocol("Transformer", plain, optional);
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        ReportCollector.Start(moduleDecl);
        ProtocolMethodDisambiguator.Compute(protocolDecl, db);
        var report = ReportCollector.Complete();

        // A type-rung family's name IS the base name the assignment loop tries first, so the recorded
        // scheme cannot be inferred by comparing the accepted name against it — the rung has to travel
        // with the entry. Get this wrong and the ledger reports a type-derived name as label-derived,
        // which is the one thing the ship gate reads to describe how the public surface was resolved.
        Assert.NotNull(report);
        var renames = report!.OverloadRenames.Where(r => r.NaturalName == "Transform").ToList();
        Assert.Equal(2, renames.Count);
        Assert.All(renames, r => Assert.Equal(nameof(OverloadNameOutcome.TypeDerived), r.Scheme));
        Assert.Contains(renames, r => r.EmittedName == "TransformWithRefBox");
        Assert.Contains(renames, r => r.EmittedName == "TransformWithOptional");
    }
}
