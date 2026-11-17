// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A member witnessing a protocol requirement implements the interface member, which keeps an
/// existential at <c>object</c>; the read-only union projection its own return position allows
/// would be CS0738 and fail the whole module.
/// </summary>
public class ProtocolRequirementWitnessTests
{
    private const string Module = "TestModule";

    private static (ModuleDecl Module, ClassDecl Parent, ClassDecl Child) Fixture(params BaseDecl[] requirements)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = Module,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var protocol = TestDecls.Protocol("Producer", Module, requirements);
        protocol.ModuleDecl = moduleDecl;
        moduleDecl.Protocols.Add(protocol);
        var parent = Class("Factory", moduleDecl);
        parent.Conformances.Add(new TypeConformance(parent.SwiftTypeName, protocol.SwiftTypeName, "$s10TestModule7FactoryCAA8ProducerAAMc"));
        var child = Class("SubFactory", moduleDecl);
        child.ResolvedSuperclass = parent;
        return (moduleDecl, parent, child);
    }

    private static ClassDecl Class(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
        MangledName = $"$s{Module.Length}{Module}{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    [Fact]
    public void MethodNamedLikeARequirement_Witnesses_OnTheConformerAndItsSubclass()
    {
        var (_, parent, child) = Fixture(TestDecls.Method("produceKeyed"));
        Assert.True(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "produceKeyed", isProperty: false));
        Assert.True(ProtocolRequirementWitness.WitnessesConformedRequirement(child, "produceKeyed", isProperty: false),
            "A subclass inherits the conformance, so its override implements the same interface member");
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "other", isProperty: false));
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "produceKeyed", isProperty: true),
            "A property is matched only against property requirements");
    }

    [Fact]
    public void PropertyNamedLikeARequirement_Witnesses()
    {
        var (_, parent, _) = Fixture(TestDecls.Property("producedKeyed"));
        Assert.True(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "producedKeyed", isProperty: true));
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "producedKeyed", isProperty: false));
    }

    [Fact]
    public void ProtocolExtensionMethod_IsNotARequirement()
    {
        var (_, parent, _) = Fixture(TestDecls.ExtensionDefault("produceKeyed"));
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(parent, "produceKeyed", isProperty: false));
    }

    [Fact]
    public void TypeWithoutConformances_WitnessesNothing()
    {
        var (moduleDecl, _, _) = Fixture(TestDecls.Method("produceKeyed"));
        var unrelated = Class("Unrelated", moduleDecl);
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(unrelated, "produceKeyed", isProperty: false));
        Assert.False(ProtocolRequirementWitness.WitnessesConformedRequirement(moduleDecl, "produceKeyed", isProperty: false));
    }
}
