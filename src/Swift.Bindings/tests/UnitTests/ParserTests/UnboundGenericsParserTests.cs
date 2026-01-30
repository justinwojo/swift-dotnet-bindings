// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for parsing unbound generic types.
/// </summary>
public class UnboundGenericsParserTests
{
    [Fact]
    public void GenericParameters_Parsed_ForStructWithSingleTypeParameter()
    {
        var signature = "<T>";
        var sugared = "<T>";

        var result = GenericSignatureParser.ParseGenericSignature(signature, sugared);

        Assert.Single(result);
        Assert.Equal("T", result[0].TypeName);
        Assert.Equal("T", result[0].SugaredTypeName);
    }

    [Fact]
    public void GenericParameters_Parsed_ForStructWithMultipleTypeParameters()
    {
        var signature = "<τ_0_0, τ_0_1>";
        var sugared = "<T, U>";

        var result = GenericSignatureParser.ParseGenericSignature(signature, sugared);

        Assert.Equal(2, result.Count);
        Assert.Equal("τ_0_0", result[0].TypeName);
        Assert.Equal("T", result[0].SugaredTypeName);
        Assert.Equal("τ_0_1", result[1].TypeName);
        Assert.Equal("U", result[1].SugaredTypeName);
    }

    [Fact]
    public void GenericParameters_IncludeConstraints_FromWhereClause()
    {
        var signature = "<τ_0_0 where τ_0_0 : Swift.Equatable>";
        var sugared = "<T where T : Swift.Equatable>";

        var result = GenericSignatureParser.ParseGenericSignature(signature, sugared);

        Assert.Single(result);
        Assert.Equal("τ_0_0", result[0].TypeName);
        Assert.Single(result[0].GenericConformances);
        Assert.Equal("Swift.Equatable", result[0].GenericConformances[0].ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void TypeDecl_IsGeneric_IsTrueWhenHasGenericParameters()
    {
        var typeDecl = new StructDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxVyxG",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule3BoxVMa",
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            }
        };

        Assert.True(typeDecl.IsGeneric);
    }

    [Fact]
    public void TypeDecl_IsGeneric_IsFalseWhenNoGenericParameters()
    {
        var typeDecl = new StructDecl
        {
            Name = "SimpleStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleStruct"),
            MangledName = "$s10TestModule12SimpleStructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule12SimpleStructVMa",
        };

        Assert.False(typeDecl.IsGeneric);
    }
}
