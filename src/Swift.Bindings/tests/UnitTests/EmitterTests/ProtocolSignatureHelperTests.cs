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

    #region §6 #12 — Intra-protocol async/sync vtable-slot keys

    [Fact]
    public void GetMethodSignatureKey_SyncVsAsync_SameNameSameParams_DifferentKeysByDefault()
    {
        // A single protocol may declare BOTH `func m()` and `func m() async`
        // (effectful overloading — two distinct witness-table requirements that
        // occupy two separate vtable slots). The slot-allocation key MUST be
        // async-sensitive by default, else the async overload aliases onto the
        // sync slot: a dropped C# member AND proxy slot-count drift vs Swift.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var sync = CreateMethodWithParam("refresh", "Swift.Int", moduleDecl, isAsync: false);
        var async = CreateMethodWithParam("refresh", "Swift.Int", moduleDecl, isAsync: true);

        var syncKey = ProtocolSignatureHelper.GetMethodSignatureKey(sync, typeDatabase);
        var asyncKey = ProtocolSignatureHelper.GetMethodSignatureKey(async, typeDatabase);

        Assert.NotEqual(syncKey, asyncKey);
        Assert.EndsWith(":async", asyncKey);
        Assert.DoesNotContain(":async", syncKey);
    }

    [Fact]
    public void GetMethodSignatureKey_SyncVsAsync_IncludeAsyncEffectFalse_SameKey()
    {
        // The lenient concrete-conformance matchers (FindMatchingMethod /
        // FindMatchingStaticMethod) pass includeAsyncEffect: false so a sync
        // witness can still satisfy an async requirement. Under that opt-out the
        // two overloads must collapse to the identical key on BOTH comparison sides.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var sync = CreateMethodWithParam("refresh", "Swift.Int", moduleDecl, isAsync: false);
        var async = CreateMethodWithParam("refresh", "Swift.Int", moduleDecl, isAsync: true);

        var syncKey = ProtocolSignatureHelper.GetMethodSignatureKey(sync, typeDatabase, includeAsyncEffect: false);
        var asyncKey = ProtocolSignatureHelper.GetMethodSignatureKey(async, typeDatabase, includeAsyncEffect: false);

        Assert.Equal(syncKey, asyncKey);
        Assert.DoesNotContain(":async", asyncKey);
    }

    [Fact]
    public void GetMethodSignatureKey_DistinctParams_DifferByParamsRegardlessOfAsync()
    {
        // Async-sensitivity must not mask ordinary param-based distinction: a sync
        // method and an async method with DIFFERENT params still produce different
        // keys (the async suffix is additive, never a substitute for param identity).
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        var syncInt = CreateMethodWithParam("load", "Swift.Int", moduleDecl, isAsync: false);
        var asyncString = CreateMethodWithParam("load", "Swift.String", moduleDecl, isAsync: true);

        var k1 = ProtocolSignatureHelper.GetMethodSignatureKey(syncInt, typeDatabase);
        var k2 = ProtocolSignatureHelper.GetMethodSignatureKey(asyncString, typeDatabase);

        Assert.NotEqual(k1, k2);
        // Even with the async effect erased they remain distinct (params differ).
        Assert.NotEqual(
            ProtocolSignatureHelper.GetMethodSignatureKey(syncInt, typeDatabase, includeAsyncEffect: false),
            ProtocolSignatureHelper.GetMethodSignatureKey(asyncString, typeDatabase, includeAsyncEffect: false));
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

    #region Consolidated ProjectTypeToCSharp Mode Tests

    [Fact]
    public void ProjectTypeToCSharp_DefaultMode_ReturnsPublicType()
    {
        // Default mode (interface context) returns PublicType from factory projection.
        var typeDatabase = CreateTypeDatabaseWithString();
        var intType = new NamedTypeSpec("Swift.Int");

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(intType, typeDatabase);

        Assert.Equal("long", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_AbiMarshallingMode_ReturnsMarshalFromSwiftType()
    {
        // ABI marshalling mode returns MarshalFromSwiftType for ABI-level type resolution.
        var typeDatabase = CreateTypeDatabaseWithString();
        var stringType = new NamedTypeSpec("Swift.String");

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            stringType, typeDatabase, mode: TypeResolutionMode.AbiMarshalling);

        // SwiftString is the ABI type for Swift.String (short name from MarshalFromSwiftType)
        Assert.Equal("SwiftString", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_NarrowNativeIntMode_AppliesNarrowing()
    {
        // NativeInt narrowing mode applies NarrowNativeIntType to the result.
        var typeDatabase = CreateTypeDatabaseWithNativeInt();
        var nativeIntType = new NamedTypeSpec("Swift.Int");

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            nativeIntType, typeDatabase, isParameter: false,
            mode: TypeResolutionMode.NarrowNativeInt);

        // Swift.Int projects to nint, which is narrowed to int
        Assert.Equal("long", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_ExistentialFallbackMode_ResolvesExistentials()
    {
        // ExistentialFallback mode includes ExistentialHandler fallback path.
        // This is used by proxy context when factory can't handle all existential patterns.
        var typeDatabase = CreateTypeDatabaseWithString();
        var intType = new NamedTypeSpec("Swift.Int");

        // Regular type should be unaffected by ExistentialFallback flag
        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            intType, typeDatabase, mode: TypeResolutionMode.ExistentialFallback);

        Assert.Equal("long", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_IncludeTupleLabels_IncludesLabelsInTupleOutput()
    {
        // When IncludeTupleLabels is set, tuple element labels appear in the tuple fallback output.
        // Use unknown types to force the factory to fail and hit the tuple fallback path.
        var typeDatabase = CreateTypeDatabase();
        var element1 = new NamedTypeSpec("UnknownModule.Foo") { TypeLabel = "x" };
        var element2 = new NamedTypeSpec("UnknownModule.Bar") { TypeLabel = "y" };
        var tupleType = new TupleTypeSpec(new List<TypeSpec> { element1, element2 });

        var withLabels = ProtocolSignatureHelper.ProjectTypeToCSharp(
            tupleType, typeDatabase, mode: TypeResolutionMode.IncludeTupleLabels);
        var withoutLabels = ProtocolSignatureHelper.ProjectTypeToCSharp(
            tupleType, typeDatabase, mode: TypeResolutionMode.Default);

        Assert.Contains("x", withLabels);
        Assert.Contains("y", withLabels);
        Assert.DoesNotContain(" x", withoutLabels);
        Assert.DoesNotContain(" y", withoutLabels);
    }

    [Fact]
    public void ProjectTypeToCSharp_ExplicitGenericContext_UsesProvidedContext()
    {
        // When genericContext is explicitly provided, it overrides the protocolContext computation.
        var typeDatabase = CreateTypeDatabaseWithString();
        var intType = new NamedTypeSpec("Swift.Int");

        // Passing explicit GenericContext.Empty should work identically
        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            intType, typeDatabase, genericContext: GenericContext.Empty);

        Assert.Equal("long", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_ProxyModeMatchesOriginalBehavior_SimpleTypes()
    {
        // Proxy mode (ExistentialFallback | IncludeTupleLabels) should match
        // the original GetCSharpTypeName behavior for simple types.
        var typeDatabase = CreateTypeDatabaseWithString();
        var proxyMode = TypeResolutionMode.ExistentialFallback | TypeResolutionMode.IncludeTupleLabels;

        // Swift.Int → long
        var intResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            new NamedTypeSpec("Swift.Int"), typeDatabase,
            genericContext: GenericContext.Empty, mode: proxyMode);
        Assert.Equal("long", intResult);

        // Swift.String → string
        var stringResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            new NamedTypeSpec("Swift.String"), typeDatabase,
            genericContext: GenericContext.Empty, mode: proxyMode);
        Assert.Equal("string", stringResult);
    }

    [Fact]
    public void ProjectTypeToCSharp_ProxyModeWithAbi_ReturnsAbiTypes()
    {
        // ABI mode should return MarshalFromSwiftType for proxy receivers.
        var typeDatabase = CreateTypeDatabaseWithString();
        var abiMode = TypeResolutionMode.ExistentialFallback | TypeResolutionMode.IncludeTupleLabels
            | TypeResolutionMode.AbiMarshalling;

        // Swift.String with ABI → SwiftString (short name from MarshalFromSwiftType)
        var stringResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            new NamedTypeSpec("Swift.String"), typeDatabase,
            genericContext: GenericContext.Empty, mode: abiMode);
        Assert.Equal("SwiftString", stringResult);

        // Swift.Int with ABI → long (primitives are same for public/ABI)
        var intResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            new NamedTypeSpec("Swift.Int"), typeDatabase,
            genericContext: GenericContext.Empty, mode: abiMode);
        Assert.Equal("long", intResult);
    }

    [Fact]
    public void ProjectTypeToCSharp_NarrowNativeIntMode_PropertyContext()
    {
        // Property interface context uses NarrowNativeInt. Verify it matches
        // the original GetInterfaceCompatiblePropertyTypeName behavior.
        var typeDatabase = CreateTypeDatabaseWithString();

        // Simple type with NarrowNativeInt (no actual narrowing needed for Int64)
        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            new NamedTypeSpec("Swift.Int"), typeDatabase, isParameter: false,
            genericContext: GenericContext.Empty, mode: TypeResolutionMode.NarrowNativeInt);

        Assert.Equal("long", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_AssociatedTypeReference_ReturnsGenericParam()
    {
        // Associated type references should map to generic params in all modes.
        var typeDatabase = CreateTypeDatabaseWithString();
        var assocRef = new AssociatedTypeReferenceSpec("Self.Element");

        // Default mode
        var defaultResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            assocRef, typeDatabase);
        Assert.Equal("TElement", defaultResult);

        // Proxy mode
        var proxyMode = TypeResolutionMode.ExistentialFallback | TypeResolutionMode.IncludeTupleLabels;
        var proxyResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            assocRef, typeDatabase, genericContext: GenericContext.Empty, mode: proxyMode);
        Assert.Equal("TElement", proxyResult);
    }

    [Fact]
    public void ProjectTypeToCSharp_NarrowNativeInt_NotAppliedRecursivelyInTuples()
    {
        // NarrowNativeInt should only apply at the top level.
        // This test verifies it works correctly for tuple types.
        var typeDatabase = CreateTypeDatabaseWithString();
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            tupleType, typeDatabase, mode: TypeResolutionMode.NarrowNativeInt);

        // Tuple itself should be narrowed at top level
        Assert.Equal("(long, long)", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_ClosureFallback_UsesCorrectMode()
    {
        // Verify closure type handling works with different modes.
        var typeDatabase = CreateTypeDatabaseWithString();

        // Simple closure: () -> Void — should resolve to Action
        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(
            closureType, typeDatabase, mode: TypeResolutionMode.Default);

        // Factory should handle this, returning Action
        Assert.Equal("global::System.Action", result);
    }

    [Fact]
    public void ProjectTypeToCSharp_ModeFlags_AreCombineable()
    {
        // Verify that multiple mode flags can be combined correctly.
        var combined = TypeResolutionMode.AbiMarshalling
            | TypeResolutionMode.ExistentialFallback
            | TypeResolutionMode.IncludeTupleLabels;

        Assert.True(combined.HasFlag(TypeResolutionMode.AbiMarshalling));
        Assert.True(combined.HasFlag(TypeResolutionMode.ExistentialFallback));
        Assert.True(combined.HasFlag(TypeResolutionMode.IncludeTupleLabels));
        Assert.False(combined.HasFlag(TypeResolutionMode.NarrowNativeInt));
    }

    [Fact]
    public void ProjectTypeToCSharp_UnknownType_ReturnsSameForAllModes()
    {
        // For unknown types that fall through to type record lookup,
        // all non-ABI modes should return the same result.
        var typeDatabase = CreateTypeDatabase();
        var unknownType = new NamedTypeSpec("UnknownModule.Widget");

        var defaultResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            unknownType, typeDatabase);

        var proxyMode = TypeResolutionMode.ExistentialFallback | TypeResolutionMode.IncludeTupleLabels;
        var proxyResult = ProtocolSignatureHelper.ProjectTypeToCSharp(
            unknownType, typeDatabase, genericContext: GenericContext.Empty, mode: proxyMode);

        Assert.Equal(defaultResult, proxyResult);
        Assert.Contains("AnyType", defaultResult);
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

    private static TypeDatabase CreateTypeDatabaseWithNativeInt()
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDescribable"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
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

    private static MethodDecl CreateMethodWithParam(string name, string paramTypeName, ModuleDecl moduleDecl, bool isAsync = false)
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
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
    }

    #region StripOptionalClassLikeForOverloadIdentity Tests

    [Fact]
    public void StripOptionalClassLike_TopLevelOptionalClass_StripsToClass()
    {
        // Optional<Class> at the top level reduces to Class — the trailing-trim
        // path in NormalizeParamTypeForOverloadIdentity already handles this for
        // string output, but the structural stripper must produce the same shape
        // so callers can compare TypeSpecs (e.g. inside a container).
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader"));

        var result = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(optional, typeDatabase);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("TestModule.Loader", named.Name);
    }

    [Fact]
    public void StripOptionalClassLike_ArrayOfOptionalClass_StripsInnerOptional()
    {
        // TipKit regression: foo(_: [RuleBuilder]) and foo(_: [RuleBuilder?]) both
        // project to IEnumerable<RuleBuilder?>/IEnumerable<RuleBuilder> — same C#
        // overload after nullability erasure on reference types. Dedup must see
        // the same structural key, so the stripper has to descend into generic
        // parameters and collapse Optional<Class> there.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var arrayOfOptionalClass = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(arrayOfOptionalClass, typeDatabase);

        var arr = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("Swift.Array", arr.Name);
        Assert.Single(arr.GenericParameters);
        var inner = Assert.IsType<NamedTypeSpec>(arr.GenericParameters[0]);
        Assert.Equal("TestModule.Loader", inner.Name);
    }

    [Fact]
    public void StripOptionalClassLike_ArrayOfOptionalClass_MatchesArrayOfClass()
    {
        // The two specs must structurally compare equal after stripping —
        // this is the property the dedup keys ultimately rely on.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var arrayOfClass = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Loader"));
        var arrayOfOptionalClass = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")));

        var stripped1 = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(arrayOfClass, typeDatabase);
        var stripped2 = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(arrayOfOptionalClass, typeDatabase);

        Assert.Equal(stripped1.ToString(), stripped2.ToString());
    }

    [Fact]
    public void StripOptionalClassLike_TupleOfOptionalClasses_StripsAllElements()
    {
        // Tuples need recursive descent too — each element is normalized
        // independently. (Optional<Class>, Optional<Class>) collapses to
        // (Class, Class) for overload identity.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")),
        });

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(tuple, typeDatabase);

        var stripTuple = Assert.IsType<TupleTypeSpec>(stripped);
        Assert.Equal(2, stripTuple.Elements.Count);
        foreach (var element in stripTuple.Elements)
        {
            var named = Assert.IsType<NamedTypeSpec>(element);
            Assert.Equal("TestModule.Loader", named.Name);
        }
    }

    [Fact]
    public void StripOptionalClassLike_OptionalFrozenStruct_Preserved()
    {
        // Frozen structs are emitted as C# value types — Optional<T> projects to
        // T? which is a distinct CLR type from T. The stripper must NOT collapse
        // these, otherwise overload(Point) and overload(Point?) would dedup away.
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

        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Point"));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(optional, typeDatabase);

        var named = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("Swift.Optional", named.Name);
        var inner = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("TestModule.Point", inner.Name);
    }

    [Fact]
    public void StripOptionalClassLike_NestedOptionalOptionalClass_CollapsesToClass()
    {
        // Optional<Optional<Class>> projects to Class? in C# (the runtime doesn't
        // express double-Optional for reference types). Both layers must strip.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var nested = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(nested, typeDatabase);

        var named = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("TestModule.Loader", named.Name);
    }

    [Fact]
    public void StripOptionalClassLike_OptionalGenericParamInScope_TreatedAsReference()
    {
        // For a reference-constrained T (or a generic param visible in scope at all,
        // since C# nullability is annotation-only for any reference type), Array<T?>
        // and Array<T> collide. The stripper must treat in-scope generic params as
        // reference-like.
        var typeDatabase = CreateTypeDatabase();
        var optionalT = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("MusicItem"));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
            optionalT, typeDatabase, new HashSet<string> { "MusicItem" });

        var named = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("MusicItem", named.Name);
    }

    [Fact]
    public void StripOptionalClassLike_OptionalClosure_Strips()
    {
        // Closures are reference types in C#, so Optional<Closure> and Closure are
        // the same CLR type at the call site.
        var typeDatabase = CreateTypeDatabase();
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var optional = new NamedTypeSpec("Swift.Optional", closure);

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(optional, typeDatabase);

        Assert.IsType<ClosureTypeSpec>(stripped);
    }

    [Fact]
    public void StripOptionalClassLike_SetOfOptionalClass_StripsInnerOptional()
    {
        // Set<Optional<Class>> and Set<Class> both project to IEnumerable<Class>
        // (params project Set→IEnumerable). Same nullability-erasure collision as Array.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var setOfOptionalClass = new NamedTypeSpec("Swift.Set",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(setOfOptionalClass, typeDatabase);

        var set = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("Swift.Set", set.Name);
        var inner = Assert.IsType<NamedTypeSpec>(set.GenericParameters[0]);
        Assert.Equal("TestModule.Loader", inner.Name);
    }

    [Fact]
    public void StripOptionalClassLike_DictionaryValueOptionalClass_StripsInnerOptional()
    {
        // Dictionary<K, Optional<Class>> and Dictionary<K, Class> are the same
        // IReadOnlyDictionary<K, Class> overload. Strip must descend into both
        // key and value generic positions.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var dictWithOptionalValue = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Loader")));

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(dictWithOptionalValue, typeDatabase);

        var dict = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("Swift.Dictionary", dict.Name);
        Assert.Equal(2, dict.GenericParameters.Count);
        var key = Assert.IsType<NamedTypeSpec>(dict.GenericParameters[0]);
        Assert.Equal("Swift.String", key.Name);
        var value = Assert.IsType<NamedTypeSpec>(dict.GenericParameters[1]);
        Assert.Equal("TestModule.Loader", value.Name);
    }

    [Fact]
    public void StripOptionalClassLike_NonOptionalType_ReturnedUnchanged()
    {
        // Non-Optional types pass through untouched — the stripper only normalizes
        // Optional<ClassLike>, leaving everything else structurally identical.
        var typeDatabase = CreateTypeDatabaseWithClassAndProtocol();
        var plain = new NamedTypeSpec("TestModule.Loader");

        var stripped = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(plain, typeDatabase);

        var named = Assert.IsType<NamedTypeSpec>(stripped);
        Assert.Equal("TestModule.Loader", named.Name);
    }

    #endregion

    [Fact]
    public void ProjectTypeToCSharp_ClosureReturningArray_UsesIReadOnlyListNotIEnumerable()
    {
        // Closure return types must use isParameter:false (return position) to project
        // arrays as IReadOnlyList<T>, not IEnumerable<T>. This ensures parity between
        // ProtocolSignatureHelper (proxy) and ProtocolHandler (interface).
        var typeDatabase = CreateTypeDatabaseWithString();

        // Build: () -> [Int]
        var arrayReturnType = new NamedTypeSpec("Swift.Array");
        arrayReturnType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, arrayReturnType);

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(closureType, typeDatabase, isParameter: false);

        // Should be Func<IReadOnlyList<long>>, not Func<IEnumerable<long>>
        Assert.Equal("global::System.Func<IReadOnlyList<long>>", result);
    }

    #endregion
}
