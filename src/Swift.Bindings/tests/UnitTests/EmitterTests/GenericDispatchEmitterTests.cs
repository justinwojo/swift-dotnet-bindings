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
