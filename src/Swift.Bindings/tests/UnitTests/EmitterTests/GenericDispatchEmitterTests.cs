// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericDispatchEmitter internal helpers — categorical guards that
/// determine whether a generic member is eligible for the GSF / static-dispatch
/// render path.
/// </summary>
public class GenericDispatchEmitterTests
{
    private static ModuleDecl CreateModule(string name = "TestModule") =>
        new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

    private static StructDecl CreateStruct(
        string name,
        ModuleDecl moduleDecl,
        BaseDecl parent,
        bool isGeneric)
    {
        var genericParameters = new List<GenericArgumentDecl>();
        if (isGeneric)
        {
            genericParameters.Add(new GenericArgumentDecl(
                "τ_0_0",
                "T",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()));
        }
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParameters,
            Conformances = new List<TypeConformance>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    // --- HasGenericOuterAncestor ---

    [Fact]
    public void HasGenericOuterAncestor_TopLevel_ReturnsFalse()
    {
        var module = CreateModule();
        var inner = CreateStruct("Leaf", module, module, isGeneric: false);

        Assert.False(GenericDispatchEmitter.HasGenericOuterAncestor(inner));
    }

    [Fact]
    public void HasGenericOuterAncestor_NestedInNonGenericOuter_ReturnsFalse()
    {
        // Inner.ParentDecl is a non-generic Outer; ancestor chain has no generic type.
        var module = CreateModule();
        var outer = CreateStruct("Outer", module, module, isGeneric: false);
        var inner = CreateStruct("Inner", module, outer, isGeneric: false);

        Assert.False(GenericDispatchEmitter.HasGenericOuterAncestor(inner));
    }

    [Fact]
    public void HasGenericOuterAncestor_NestedInGenericOuter_ReturnsTrue()
    {
        // The dotted construction expression places generic args on the wrong segment
        // when the outer is generic — gate must trip.
        var module = CreateModule();
        var outer = CreateStruct("Outer", module, module, isGeneric: true);
        var inner = CreateStruct("Inner", module, outer, isGeneric: false);

        Assert.True(GenericDispatchEmitter.HasGenericOuterAncestor(inner));
    }

    [Fact]
    public void HasGenericOuterAncestor_DeeplyNestedWithGenericGrandparent_ReturnsTrue()
    {
        // Generic ancestor at depth 2 still trips the gate — the walk doesn't stop at
        // the direct parent.
        var module = CreateModule();
        var grand = CreateStruct("Grand", module, module, isGeneric: true);
        var middle = CreateStruct("Middle", module, grand, isGeneric: false);
        var leaf = CreateStruct("Leaf", module, middle, isGeneric: false);

        Assert.True(GenericDispatchEmitter.HasGenericOuterAncestor(leaf));
    }

    [Fact]
    public void HasGenericOuterAncestor_GenericSelfNonGenericOuter_ReturnsFalse()
    {
        // The check inspects ANCESTORS only — the parent type's own genericity does
        // not count. Inner generic + Outer non-generic is a case that the gate must NOT
        // block (single-segment generic args are well-formed for the host itself).
        var module = CreateModule();
        var outer = CreateStruct("Outer", module, module, isGeneric: false);
        var inner = CreateStruct("Inner", module, outer, isGeneric: true);

        Assert.False(GenericDispatchEmitter.HasGenericOuterAncestor(inner));
    }

    // --- OuterMatchesParent ---

    [Fact]
    public void OuterMatchesParent_SimpleName_MatchesByShortName()
    {
        // No module prefix on the outer spec — short-name equality applies.
        var module = CreateModule();
        var parent = CreateStruct("Builder", module, module, isGeneric: true);
        var spec = new NamedTypeSpec("Builder");

        Assert.True(GenericDispatchEmitter.OuterMatchesParent(spec, parent));
    }

    [Fact]
    public void OuterMatchesParent_ModuleQualified_RequiresExactQualifiedMatch()
    {
        // Outer spec carries module prefix — only the exact module-qualified name
        // matches. The short-name fallback must NOT engage.
        var module = CreateModule();
        var parent = CreateStruct("Builder", module, module, isGeneric: true);
        var matching = new NamedTypeSpec("TestModule.Builder");

        Assert.True(GenericDispatchEmitter.OuterMatchesParent(matching, parent));
    }

    [Fact]
    public void OuterMatchesParent_ModuleQualified_DifferentModule_ReturnsFalse()
    {
        // Cross-host: another module's short-name collision (OtherModule.Builder)
        // must NOT match the parent (TestModule.Builder). This is the regression
        // the module-qualified guard was added for.
        var module = CreateModule();
        var parent = CreateStruct("Builder", module, module, isGeneric: true);
        var crossHost = new NamedTypeSpec("OtherModule.Builder");

        Assert.False(GenericDispatchEmitter.OuterMatchesParent(crossHost, parent));
    }

    [Fact]
    public void OuterMatchesParent_SimpleName_DifferentShortName_ReturnsFalse()
    {
        var module = CreateModule();
        var parent = CreateStruct("Builder", module, module, isGeneric: true);
        var unrelated = new NamedTypeSpec("Container");

        Assert.False(GenericDispatchEmitter.OuterMatchesParent(unrelated, parent));
    }
}
