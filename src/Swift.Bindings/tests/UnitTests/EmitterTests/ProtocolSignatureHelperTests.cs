// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolSignatureHelper projected C# key generation.
/// </summary>
public class ProtocolSignatureHelperTests
{
    #region A6 — Projected C# Key Tests

    [Fact]
    public void GetProjectedCSharpMethodKey_AnyTypeFallbackCollapse_SameKey()
    {
        // Two methods with different unresolvable types both collapse to AnyType,
        // producing the same projected C# method key.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Method 1: param is UnknownModule.Foo → AnyType
        var method1 = CreateMethodWithParam("doWork", "UnknownModule.Foo", moduleDecl);
        // Method 2: param is UnknownModule.Bar → AnyType
        var method2 = CreateMethodWithParam("doWork", "UnknownModule.Bar", moduleDecl);

        var key1 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method1, typeDatabase);
        var key2 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method2, typeDatabase);

        Assert.Equal(key1, key2);
        Assert.Contains("AnyType", key1);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_IdiomaticTypeNormalization_MatchesEmission()
    {
        // SwiftString → string, ensuring projected key uses idiomatic C# names.
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = CreateMethodWithParam("process", "Swift.String", moduleDecl);
        var key = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, typeDatabase);

        // Should use idiomatic "string" not "SwiftString"
        Assert.Equal("Process(string)", key);
    }

    #endregion

    #region P1 Fix — isParameter + Native Remapping

    [Fact]
    public void GetProjectedCSharpMethodKey_ArrayParam_UsesIEnumerableNotIReadOnlyList()
    {
        // Array parameter should project to IEnumerable<T> (isParameter=true),
        // not IReadOnlyList<T> (isParameter=false).
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create Swift.Array<Swift.Int> type spec
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModuleprocessyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty, PrivateName = string.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = arrayTypeSpec,
                    Name = "items", PrivateName = "items",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = moduleDecl,
            Throws = false, IsAsync = false,
            Visibility = Visibility.Public
        };

        var key = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, typeDatabase);

        // Parameters use IEnumerable, not IReadOnlyList
        Assert.Equal("Process(IEnumerable<System.Int64>)", key);
    }

    [Fact]
    public void ProjectTypeToCSharp_ArrayAsReturn_UsesIReadOnlyList()
    {
        // Array as return type should project to IReadOnlyList<T> (isParameter=false).
        var typeDatabase = CreateTypeDatabaseWithString();

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(arrayTypeSpec, typeDatabase, isParameter: false);

        Assert.Equal("IReadOnlyList<System.Int64>", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_NativeTypeRemapping_ReturnsNativeTypeName()
    {
        // Foundation.URL with native remapping should project to NSUrl.
        var typeDatabase = CreateTypeDatabaseWithNativeRemapping();

        var urlTypeSpec = new NamedTypeSpec("Foundation.URL");

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(urlTypeSpec, typeDatabase);

        Assert.Equal("Foundation.NSUrl", result);
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithNativeRemapping()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        // Register Foundation.URL with NativeTypeName → NSUrl
        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(foundationModule);
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
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
    }

    private static MethodDecl CreateMethodWithParam(string name, string paramTypeName, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec(paramTypeName),
                    Name = "input",
                    PrivateName = "input",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion
}
