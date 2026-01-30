// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for generic method emission including where clauses.
/// </summary>
public class GenericMethodEmitterTests
{
    #region NameProvider.GetGenericTypeMapping Tests

    [Fact]
    public void GetGenericTypeMapping_SingleParameter_ReturnsT0()
    {
        var methodDecl = CreateGenericMethodDecl(new[] { "τ_0_0" });

        var mapping = NameProvider.GetGenericTypeMapping(methodDecl);

        Assert.Single(mapping);
        Assert.True(mapping.ContainsKey("τ_0_0"));
        Assert.Equal("T0", mapping["τ_0_0"].TypeParameter);
    }

    [Fact]
    public void GetGenericTypeMapping_MultipleParameters_ReturnsT0T1T2()
    {
        var methodDecl = CreateGenericMethodDecl(new[] { "τ_0_0", "τ_0_1", "τ_0_2" });

        var mapping = NameProvider.GetGenericTypeMapping(methodDecl);

        Assert.Equal(3, mapping.Count);
        Assert.Equal("T0", mapping["τ_0_0"].TypeParameter);
        Assert.Equal("T1", mapping["τ_0_1"].TypeParameter);
        Assert.Equal("T2", mapping["τ_0_2"].TypeParameter);
    }

    [Fact]
    public void GetGenericTypeMapping_EmptyParameters_ReturnsEmptyMapping()
    {
        var methodDecl = CreateGenericMethodDecl(Array.Empty<string>());

        var mapping = NameProvider.GetGenericTypeMapping(methodDecl);

        Assert.Empty(mapping);
    }

    #endregion

    #region GenericArgumentDecl Creation Tests

    [Fact]
    public void GenericArgumentDecl_WithNoConformances_HasEmptyConformanceList()
    {
        var decl = new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()
        );

        Assert.Equal("τ_0_0", decl.TypeName);
        Assert.Equal("T", decl.SugaredTypeName);
        Assert.Empty(decl.GenericConformances);
    }

    [Fact]
    public void GenericArgumentDecl_WithProtocolConformance_HasConformance()
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName("Nuke.ImageProcessing"),
            ConformanceKind.Protocol
        );

        var decl = new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance> { conformance },
            new List<GenericParameterConformance>()
        );

        Assert.Single(decl.GenericConformances);
        Assert.Equal("Nuke.ImageProcessing", decl.GenericConformances[0].ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void GenericArgumentDecl_WithMultipleConformances_HasAllConformances()
    {
        var conformance1 = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            ConformanceKind.Protocol
        );
        var conformance2 = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            ConformanceKind.Protocol
        );

        var decl = new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance> { conformance1, conformance2 },
            new List<GenericParameterConformance>()
        );

        Assert.Equal(2, decl.GenericConformances.Count);
    }

    #endregion

    #region NameProvider.GetInterfaceName Tests

    [Fact]
    public void GetInterfaceName_StandardProtocol_ReturnsISwiftPrefixedName()
    {
        var result = NameProvider.GetInterfaceName("ImageProcessing");

        Assert.Equal("ISwiftImageProcessing", result);
    }

    [Fact]
    public void GetInterfaceName_Equatable_ReturnsIEquatableGeneric()
    {
        var result = NameProvider.GetInterfaceName("Equatable", "MyType");

        Assert.Equal("IEquatable<MyType>", result);
    }

    [Fact]
    public void GetInterfaceName_SwiftProtocol_ReturnsISwiftPrefixed()
    {
        var result = NameProvider.GetInterfaceName("Sendable");

        Assert.Equal("ISwiftSendable", result);
    }

    #endregion

    #region NameProvider Protocol Witness Table Tests

    [Fact]
    public void GetProtocolWitnessTableName_ReturnsCorrectFormat()
    {
        var result = NameProvider.GetProtocolWitnessTableName("T0", "ImageProcessing");

        Assert.Equal("T0ImageProcessingPWT", result);
    }

    [Fact]
    public void GetMetadataName_ReturnsCorrectFormat()
    {
        var result = NameProvider.GetMetadataName("T0");

        Assert.Equal("T0Metadata", result);
    }

    #endregion

    #region MethodDecl.IsGeneric Tests

    [Fact]
    public void MethodDecl_WithGenericParameters_IsGenericReturnsTrue()
    {
        var methodDecl = CreateGenericMethodDecl(new[] { "τ_0_0" });

        Assert.True(methodDecl.IsGeneric);
    }

    [Fact]
    public void MethodDecl_WithoutGenericParameters_IsGenericReturnsFalse()
    {
        var methodDecl = CreateGenericMethodDecl(Array.Empty<string>());

        Assert.False(methodDecl.IsGeneric);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateGenericMethodDecl(string[] genericParamNames)
    {
        var genericParams = genericParamNames.Select(name =>
            new GenericArgumentDecl(
                name,
                name,
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()
            )
        ).ToList();

        return new MethodDecl
        {
            Name = "TestMethod",
            MangledName = "$s10TestModule10TestMethodyxxlF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = genericParams,
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateGenericMethodDeclWithConformances(
        string[] genericParamNames,
        Dictionary<string, string[]> conformances)
    {
        var genericParams = genericParamNames.Select(name =>
        {
            var paramConformances = conformances.TryGetValue(name, out var protos)
                ? protos.Select(p => new GenericParameterConformance(
                    new[] { name },
                    SwiftTypeName.FromModuleQualifiedName(p),
                    ConformanceKind.Protocol
                )).ToList()
                : new List<GenericParameterConformance>();

            return new GenericArgumentDecl(
                name,
                name,
                paramConformances,
                new List<GenericParameterConformance>()
            );
        }).ToList();

        return new MethodDecl
        {
            Name = "TestMethod",
            MangledName = "$s10TestModule10TestMethodyxxlF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = genericParams,
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion
}
