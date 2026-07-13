// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the concrete-specialization (CSM) emitter's handling of a generic parent whose member
/// is typed through a member typealias onto an associated type — the ABI prints the self-qualified
/// form <c>Parent&lt;Action&gt;.Assoc</c> / <c>Parent&lt;Action&gt;.Assoc?</c>. Two behaviours are
/// pinned:
///   1. <see cref="ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec"/>
///      resolves the associated-type leaf carried in <c>InnerType</c> to the conformer's concrete
///      witness (lifting any Optional to the outer spec), instead of silently dropping the InnerType
///      and collapsing the node to the bare parent generic. The old code dropped the InnerType, so
///      these assertions are behaviourally red against it.
///   2. <see cref="ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn"/> recognises the
///      uninhabited <c>Never</c> / <c>Optional&lt;Never&gt;</c> shapes the substitution can produce
///      (a conformer that leaves the associated type at its default <c>= Never</c>), which the
///      emitter uses to omit the impossible member rather than emit an uncompilable wrapper.
/// The witnesses are keyed purely on the conformer's associated-type map — never on a type or module
/// name — so a conformer that binds the associated type to a real, inhabited type keeps a concrete,
/// non-suppressed projection (the control assertions).
/// </summary>
public class ConcreteSpecializationNeverAssociatedTypeTests
{
    private const string CarrierProtocol = "SwiftBindingsTestLib.PayloadCarrier";
    private const string ParentGeneric = "SwiftBindingsTestLib.CarrierDefinition";

    // ── IsUninhabitedNeverReturn: the omission predicate ───────────────────────────────

    [Fact]
    public void IsUninhabitedNeverReturn_BareNever_True()
    {
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(
            new NamedTypeSpec("Swift.Never")));
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(
            new NamedTypeSpec("Never")));
    }

    [Fact]
    public void IsUninhabitedNeverReturn_OptionalOfNever_True()
    {
        var optionalNever = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Never"));
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(optionalNever));

        // Nested Optional<Optional<Never>> is still uninhabited-only-by-nil.
        var doubleOptionalNever = new NamedTypeSpec("Optional", optionalNever);
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(doubleOptionalNever));
    }

    [Fact]
    public void IsUninhabitedNeverReturn_InhabitedTypes_False()
    {
        // A real value type and its Optional are inhabited — must NOT be omitted.
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(
            new NamedTypeSpec("Swift.Int32")));
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int32"))));

        // Array<Never> is inhabited (the empty array) — the predicate keys on the top-level
        // Never/Optional<Never> shape, not on Never appearing anywhere.
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Never"))));
    }

    // ── SubstitutePairingGenericsInTypeSpec: associated-type InnerType resolution ───────

    [Fact]
    public void Substitute_OptionalSelfQualifiedAssociatedType_NeverConformer_ResolvesToOptionalNever()
    {
        // Optional< CarrierDefinition<Action>.Payload > with a conformer that leaves Payload = Never.
        var typeSpec = OptionalOfSelfQualified("Payload");
        var pairing = Pairing(new Dictionary<string, string> { ["Payload"] = "Swift.Never" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            typeSpec, pairing);

        // The self-qualified member resolves to the concrete witness with Optional lifted to the
        // outer spec: Optional<Never> — NOT the bare parent generic the old code produced.
        var optional = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", optional.Name);
        Assert.Single(optional.GenericParameters);
        Assert.Equal("Swift.Never", Assert.IsType<NamedTypeSpec>(optional.GenericParameters[0]).Name);
        Assert.Null(optional.InnerType);

        // …and that resolved shape is what the omission gate recognises as uninhabited.
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(result));
    }

    [Fact]
    public void Substitute_OptionalSelfQualifiedAssociatedType_RealConformer_ResolvesToOptionalReal()
    {
        // Control: a conformer that binds Payload to a real, inhabited type is projected concretely
        // and is NOT flagged uninhabited — proving the omission keys on Never, not on the shape.
        var typeSpec = OptionalOfSelfQualified("Payload");
        var pairing = Pairing(new Dictionary<string, string> { ["Payload"] = "Swift.Int32" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            typeSpec, pairing);

        var optional = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", optional.Name);
        Assert.Equal("Swift.Int32", Assert.IsType<NamedTypeSpec>(optional.GenericParameters[0]).Name);
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(result));
    }

    [Fact]
    public void Substitute_InnerOptionalSelfQualifiedAssociatedType_NeverConformer_ResolvesToOptionalNever()
    {
        // The REAL RealityFoundation shape: the ABI prints `ActionEventDefinition<ActionType>.EventParameterType?`,
        // which the parser builds as the parent generic carrying the Optional-wrapped associated type in
        // its InnerType — NOT an outer Optional. The Optional lives on InnerType and must be lifted to the
        // outer resolved spec. This is the path `TryResolveAssociatedInnerType` unwraps, distinct from the
        // outer-Optional form exercised above.
        var typeSpec = SelfQualifiedInnerOptional("Payload");
        var pairing = Pairing(new Dictionary<string, string> { ["Payload"] = "Swift.Never" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            typeSpec, pairing);

        var optional = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", optional.Name);
        Assert.Single(optional.GenericParameters);
        Assert.Equal("Swift.Never", Assert.IsType<NamedTypeSpec>(optional.GenericParameters[0]).Name);
        Assert.Null(optional.InnerType);
        Assert.True(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(result));
    }

    [Fact]
    public void Substitute_InnerOptionalSelfQualifiedAssociatedType_RealConformer_ResolvesToOptionalReal()
    {
        // Control for the RF shape: an inhabited witness yields a concrete Optional<Int32>, not uninhabited.
        var typeSpec = SelfQualifiedInnerOptional("Payload");
        var pairing = Pairing(new Dictionary<string, string> { ["Payload"] = "Swift.Int32" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            typeSpec, pairing);

        var optional = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", optional.Name);
        Assert.Equal("Swift.Int32", Assert.IsType<NamedTypeSpec>(optional.GenericParameters[0]).Name);
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(result));
    }

    [Fact]
    public void Substitute_PairingGenericOnlyInInnerLeaf_DoesNotResolveWitness()
    {
        // Negative: the pairing generic appears ONLY in the InnerType leaf, not in the parent's own
        // generic arguments (`Wrapper.Action` — no outer generic args). This is not a parent-generic
        // being specialized over the conformer, so the associated-type resolution must NOT fire even
        // though the leaf name would otherwise be looked up. Pins the guard's outer-node scoping.
        var node = new NamedTypeSpec("SwiftBindingsTestLib.Wrapper")
        {
            InnerType = new NamedTypeSpec("Action"),
        };
        // Map keyed on the leaf name so a broken guard would (wrongly) rewrite it to the witness.
        var pairing = Pairing(new Dictionary<string, string> { ["Action"] = "Swift.Never" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            node, pairing);

        // Unchanged: still the bare parent with its InnerType leaf intact — no witness resolution.
        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("SwiftBindingsTestLib.Wrapper", named.Name);
        Assert.NotNull(named.InnerType);
        Assert.Equal("Action", Assert.IsType<NamedTypeSpec>(named.InnerType!).Name);
    }

    [Fact]
    public void Substitute_NonOptionalSelfQualifiedAssociatedType_RealConformer_ResolvesToWitness()
    {
        // The must-stay-green control member: a NON-optional associated-type reference
        // CarrierDefinition<Action>.Metric with a real witness resolves to that witness directly.
        var typeSpec = SelfQualified("Metric");
        var pairing = Pairing(new Dictionary<string, string> { ["Metric"] = "Swift.Int32" });

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(
            typeSpec, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Int32", named.Name);
        Assert.Empty(named.GenericParameters);
        Assert.Null(named.InnerType);
        Assert.False(ConcreteProtocolSpecializationEmitter.IsUninhabitedNeverReturn(result));
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────

    /// <summary>CarrierDefinition&lt;Action&gt;.{assoc} — the self-qualified associated-type node.</summary>
    private static NamedTypeSpec SelfQualified(string assocName)
    {
        var parent = new NamedTypeSpec(ParentGeneric, new NamedTypeSpec("Action"))
        {
            InnerType = new NamedTypeSpec(assocName),
        };
        return parent;
    }

    /// <summary>Optional&lt; CarrierDefinition&lt;Action&gt;.{assoc} &gt; — the outer-Optional stored-property shape.</summary>
    private static NamedTypeSpec OptionalOfSelfQualified(string assocName) =>
        new("Swift.Optional", SelfQualified(assocName));

    /// <summary>
    /// CarrierDefinition&lt;Action&gt;.{assoc}? — the parser output for the ABI printed name
    /// <c>Parent&lt;Action&gt;.Assoc?</c>: the parent generic with an Optional-wrapped associated-type
    /// leaf in its InnerType (the real RealityFoundation <c>ActionEventDefinition&lt;A&gt;.EventParameterType?</c>
    /// shape), as opposed to an outer Optional.
    /// </summary>
    private static NamedTypeSpec SelfQualifiedInnerOptional(string assocName) =>
        new(ParentGeneric, new NamedTypeSpec("Action"))
        {
            InnerType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(assocName)),
        };

    private static (ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)[]
        Pairing(IReadOnlyDictionary<string, string> associatedTypes)
    {
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "SwiftBindingsTestLib.SilentCarrier",
            CSharpType: "SilentCarrier",
            AssociatedTypes: associatedTypes);
        var genericParam = new GenericArgumentDecl(
            TypeName: "Action",
            SugaredTypeName: "Action",
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
        var param = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: genericParam,
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName(CarrierProtocol),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            IsParentGeneric: true);
        return new[] { (param, conformer) };
    }
}
