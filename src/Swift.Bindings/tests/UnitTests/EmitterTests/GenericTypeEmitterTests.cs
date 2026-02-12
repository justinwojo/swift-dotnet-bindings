// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericTypeEmitter.
/// </summary>
public class GenericTypeEmitterTests
{
    [Fact]
    public void GetGenericParameterList_ReturnsEmpty_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetGenericParameterList_ReturnsSingleParam_ForSingleGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal("<T0>", result);
    }

    [Fact]
    public void GetGenericParameterList_ReturnsMultipleParams_ForMultipleGenericType()
    {
        var typeDecl = CreateGenericStruct("Pair", 2);

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal("<T0, T1>", result);
    }

    [Fact]
    public void GetTypeNameWithGenerics_ReturnsNameOnly_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl);

        Assert.Equal("SimpleStruct", result);
    }

    [Fact]
    public void GetTypeNameWithGenerics_ReturnsNameWithParams_ForGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl);

        Assert.Equal("Box<T0>", result);
    }

    [Fact]
    public void GetWhereClause_ReturnsEmpty_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetWhereClause_ReturnsISwiftObjectConstraint_ForGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal("where T0 : ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_ReturnsMultipleConstraints_ForMultipleGenericParams()
    {
        var typeDecl = CreateGenericStruct("Pair", 2);

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal("where T0 : ISwiftObject where T1 : ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_IncludesProtocolConstraints()
    {
        var typeDecl = CreateGenericStructWithConstraints("Container", new List<string> { "Swift.Equatable" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Contains("ISwiftObject", result);
        // Swift.Equatable maps to IEquatable<> in C# (with empty type name when called from GetWhereClause)
        Assert.Contains("IEquatable<>", result);
    }

    [Fact]
    public void GetWhereClause_SkipsSendableConstraint()
    {
        var typeDecl = CreateGenericStructWithConstraints("AsyncBox", new List<string> { "Swift.Sendable" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.DoesNotContain("Sendable", result);
    }

    [Fact]
    public void GetWhereClause_SkipsUnsupportedSwiftUIConstraint()
    {
        var typeDecl = CreateGenericStructWithConstraints("UIBox", new List<string> { "SwiftUI.View" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal("where T0 : ISwiftObject", result);
        Assert.DoesNotContain("ISwiftView", result);
    }

    [Fact]
    public void TryGetUnsupportedConstraint_ReturnsTrue_ForSwiftUIProtocol()
    {
        var typeDecl = CreateGenericStructWithConstraints("UIBox", new List<string> { "SwiftUI.View" });

        var found = GenericTypeEmitter.TryGetUnsupportedConstraint(typeDecl, out var unsupportedConstraint);

        Assert.True(found);
        Assert.NotNull(unsupportedConstraint);
        Assert.Equal("View", unsupportedConstraint.Name);
        Assert.Equal("SwiftUI", unsupportedConstraint.Module);
    }

    [Fact]
    public void GetFullTypeSignature_ReturnsNameOnly_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetFullTypeSignature(typeDecl);

        Assert.Equal("SimpleStruct", result);
    }

    [Fact]
    public void GetFullTypeSignature_ReturnsNameWithWhereClause_ForGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetFullTypeSignature(typeDecl);

        Assert.Equal("Box<T0> where T0 : ISwiftObject", result);
    }

    #region Cross-Module Constraint Stripping Tests

    [Fact]
    public void GetWhereClause_StdlibDecodableConstraint_IsStripped()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("RequestInterceptor", "Alamofire",
            new List<string> { "Swift.Decodable" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.DoesNotContain("Decodable", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_StdlibErrorConstraint_IsStripped()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("ErrorWrapper", "Alamofire",
            new List<string> { "Swift.Error" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.DoesNotContain("Error", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_SameModuleProtocol_IsKept()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Container", "Alamofire",
            new List<string> { "Alamofire.RequestInterceptor" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Same-module constraint is kept even without TypeDB registration
        Assert.Contains("IRequestInterceptor", result);
    }

    [Fact]
    public void GetWhereClause_CrossModuleRegisteredProtocol_IsKept()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Wrapper", "Alamofire",
            new List<string> { "Foundation.NSCoding" });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Foundation.NSCoding"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "INSCoding"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSCoding"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.Contains("INSCoding", result);
    }

    [Fact]
    public void GetWhereClause_MultipleMixedConstraints_OnlyKnownKept()
    {
        // T has both Decodable (cross-module, unregistered) and ISwiftObject (baseline)
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Swift.Decodable"),
                ConformanceKind.Protocol),
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Alamofire.RequestInterceptor"),
                ConformanceKind.Protocol)
        };

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", conformances, new List<GenericParameterConformance>())
        };

        var moduleDecl = CreateModuleDecl("Alamofire");
        var typeDecl = new StructDecl
        {
            Name = "MixedBox",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.MixedBox"),
            MangledName = "$s9Alamofire8MixedBoxV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s9Alamofire8MixedBoxVMa",
            GenericParameters = genericParams
        };

        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.DoesNotContain("Decodable", result);
        Assert.Contains("IRequestInterceptor", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_NoTypeDatabase_EmitsAll()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Box", "Alamofire",
            new List<string> { "Swift.Decodable" });

        // null typeDatabase → preserves existing behavior (no filtering)
        var result = GenericTypeEmitter.GetWhereClause(typeDecl, null);

        Assert.Contains("IDecodable", result);
    }

    #endregion

    private static StructDecl CreateNonGenericStruct()
    {
        return new StructDecl
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
    }

    private static StructDecl CreateGenericStruct(string name, int typeParamCount)
    {
        var genericParams = new List<GenericArgumentDecl>();
        for (int i = 0; i < typeParamCount; i++)
        {
            genericParams.Add(new GenericArgumentDecl(
                $"τ_0_{i}",
                $"T{i}",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()
            ));
        }

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            GenericParameters = genericParams
        };
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateGenericStructWithConstraintsAndModule(string name, string moduleName, List<string> protocols)
    {
        var conformances = protocols.Select(p => new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(p),
            ConformanceKind.Protocol
        )).ToList();

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "τ_0_0",
                "T",
                conformances,
                new List<GenericParameterConformance>()
            )
        };

        var moduleDecl = CreateModuleDecl(moduleName);

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VMa",
            GenericParameters = genericParams
        };
    }

    private static StructDecl CreateGenericStructWithConstraints(string name, List<string> protocols)
    {
        var conformances = protocols.Select(p => new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(p),
            ConformanceKind.Protocol
        )).ToList();

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "τ_0_0",
                "T",
                conformances,
                new List<GenericParameterConformance>()
            )
        };

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            GenericParameters = genericParams
        };
    }
}
