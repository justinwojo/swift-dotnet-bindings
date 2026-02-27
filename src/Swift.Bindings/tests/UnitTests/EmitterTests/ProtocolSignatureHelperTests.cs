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
        Assert.Equal("Process(IEnumerable<long>)", key);
    }

    [Fact]
    public void ProjectTypeToCSharp_ArrayAsReturn_UsesIReadOnlyList()
    {
        // Array as return type should project to IReadOnlyList<T> (isParameter=false).
        var typeDatabase = CreateTypeDatabaseWithString();

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(arrayTypeSpec, typeDatabase, isParameter: false);

        Assert.Equal("IReadOnlyList<long>", result);
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

    [Fact]
    public void ProjectTypeToCSharp_UnrecognizedBoundGeneric_ReturnsAnyType()
    {
        // SwiftDictionary<K,V> has ContainsGenericParameters=true but BoundGenericsHandler
        // doesn't recognize it. Should return AnyType, not bare type name without args.
        var typeDatabase = CreateTypeDatabase();

        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("UnknownModule.Foo"));

        // Should not throw NotSupportedException
        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(dictTypeSpec, typeDatabase);

        // Returns AnyType instead of bare "SwiftDictionary" (which causes CS0305)
        Assert.Contains("AnyType", result);
    }

    #endregion

    #region Dictionary Generic Arg Preservation (typeTranslator fix)

    [Fact]
    public void GetProjectedCSharpMethodKey_OptionalDictionaryClosure_PreservesGenericArgs()
    {
        // Bug fix: GetProjectedCSharpMethodKey must preserve generic args on SwiftDictionary
        // when used inside a closure parameter. Without the typeTranslator fix (line 155),
        // GetElementType falls back to bare type lookup and loses the generic params.
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Closure: (Optional<Dictionary<AnyHashable, Int>>, Optional<Bool>) -> Void
        var closureParams = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Dictionary",
                new NamedTypeSpec("Swift.AnyHashable"),
                new NamedTypeSpec("Swift.Int"))),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"))
        });
        var closureType = new ClosureTypeSpec(closureParams, TupleTypeSpec.Empty);

        var method = new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$s10TestModulefetchDatayyF",
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
                    SwiftTypeSpec = closureType,
                    Name = "completion", PrivateName = "completion",
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

        // Key must contain projected dictionary type with generic args
        Assert.Contains("IReadOnlyDictionary<", key);
        // Must NOT have bare type without generic args
        Assert.DoesNotContain("IReadOnlyDictionary,", key);
        Assert.DoesNotContain("IReadOnlyDictionary>", key);
    }

    private static TypeDatabase CreateTypeDatabaseWithDictionary()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftAnyHashable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion

    #region NormalizeParamTypeForOverloadIdentity Tests

    [Fact]
    public void NormalizeParamType_OptionalClass_StripsNullable()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Loader"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "Loader?", optionalType, typeDatabase);

        Assert.Equal("Loader", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalProtocol_StripsNullable()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Describable"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "IDescribable?", optionalType, typeDatabase);

        Assert.Equal("IDescribable", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalComplexEnum_StripsNullable()
    {
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Variant"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "Variant?", optionalType, typeDatabase);

        Assert.Equal("Variant", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalStruct_PreservesNullable()
    {
        var typeDatabase = CreateTypeDatabase();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "long?", optionalType, typeDatabase);

        // Value types (structs) preserve the ? — not stripped
        Assert.Equal("long?", result);
    }

    [Fact]
    public void NormalizeParamType_NonOptional_ReturnsSameString()
    {
        var typeDatabase = CreateTypeDatabase();
        var namedType = new NamedTypeSpec("Swift.Int");

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "long", namedType, typeDatabase);

        Assert.Equal("long", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalString_StripsNullable()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "string?", optionalType, typeDatabase);

        // String projects to 'string' (reference type in C#) — nullable annotation stripped
        Assert.Equal("string", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalObject_StripsNullable()
    {
        var typeDatabase = CreateTypeDatabase();
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("UnknownModule.SomeType"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "object?", optionalType, typeDatabase);

        // 'object' is a reference type in C# — nullable annotation stripped
        Assert.Equal("object", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalNonFrozenStruct_StripsNullable()
    {
        // Non-frozen structs are emitted as C# classes (ClassWithOpaquePayload),
        // so Optional<NonFrozenStruct> and NonFrozenStruct are the same CLR type.
        // Reproduces: Parchment PageBuilder.BuildExpression(Page) vs BuildExpression(Page?)
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Page"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Page"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Page"),
                MetadataAccessor = "$s10TestModule4PageVMa",
                Flags = TypeRecordFlags.None, // NOT frozen — emits as C# class
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(module);

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Page"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "Page?", optionalType, typeDatabase);

        // Non-frozen struct → C# class → nullable annotation stripped
        Assert.Equal("Page", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalFrozenStruct_PreservesNullable()
    {
        // Frozen structs are emitted as C# structs, where T? and T are different
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(module);

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Point"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "Point?", optionalType, typeDatabase);

        // Frozen struct → C# value type → nullable preserved
        Assert.Equal("Point?", result);
    }

    [Fact]
    public void NormalizeParamType_OptionalFrozenStructProjectedAsClass_StripsNullable()
    {
        // Frozen structs with RequiresMemoryManagement are emitted as C# classes (ClassWithBufferStruct),
        // so Optional<T> and T produce the same CLR type — nullable must be stripped.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(module);

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("TestModule.Widget"));

        var result = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
            "Widget?", optionalType, typeDatabase);

        // Frozen + RequiresMemoryManagement → C# class → nullable annotation stripped
        Assert.Equal("Widget", result);
    }

    #endregion

    #region NormalizeContainerForOverloadKey Tests

    [Fact]
    public void NormalizeContainer_ArrayWithGenericParam_ReturnsIEnumerable()
    {
        var typeDatabase = new TypeDatabase();
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var result = BaseHandler.NormalizeContainerForOverloadKey(arrayType, typeDatabase);

        Assert.Equal("IEnumerable<τ_0_0>", result);
    }

    [Fact]
    public void NormalizeContainer_SetWithGenericParam_ReturnsIEnumerable()
    {
        var typeDatabase = new TypeDatabase();
        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var result = BaseHandler.NormalizeContainerForOverloadKey(setType, typeDatabase);

        Assert.Equal("IEnumerable<τ_0_0>", result);
    }

    [Fact]
    public void NormalizeContainer_ArrayAndSet_ProduceSameKey()
    {
        // Reproduces: ObjectMapper Mapper.toJSONString(Array<N>) vs toJSONString(Set<N>)
        // Both project to IEnumerable<N> — must produce same dedup key
        var typeDatabase = new TypeDatabase();
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var arrayResult = BaseHandler.NormalizeContainerForOverloadKey(arrayType, typeDatabase);
        var setResult = BaseHandler.NormalizeContainerForOverloadKey(setType, typeDatabase);

        Assert.Equal(arrayResult, setResult);
    }

    [Fact]
    public void NormalizeContainer_DictionaryWithGenericParams_ReturnsIReadOnlyDictionary()
    {
        var typeDatabase = new TypeDatabase();
        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        dictType.GenericParameters.Add(new NamedTypeSpec("τ_0_1"));

        var result = BaseHandler.NormalizeContainerForOverloadKey(dictType, typeDatabase);

        Assert.Equal("IReadOnlyDictionary<τ_0_0,τ_0_1>", result);
    }

    [Fact]
    public void NormalizeContainer_NonContainerType_FallsToDbLookup()
    {
        var typeDatabase = new TypeDatabase();
        var namedType = new NamedTypeSpec("TestModule.Widget");

        var result = BaseHandler.NormalizeContainerForOverloadKey(namedType, typeDatabase);

        // No DB record → AnyType
        Assert.Equal("Swift.AnyType", result);
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

    private static TypeDatabase CreateTypeDatabaseWithClassAndProtocol()
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IDescribable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithComplexEnum()
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);
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
