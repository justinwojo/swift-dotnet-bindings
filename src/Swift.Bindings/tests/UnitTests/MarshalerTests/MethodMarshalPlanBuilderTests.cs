// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for MethodMarshalPlanBuilder.
/// Tests that the builder correctly extracts method-level concerns into SyncMethodPlan.
/// </summary>
public class MethodMarshalPlanBuilderTests
{
    #region SwiftSelf Tests

    [Fact]
    public void SwiftSelf_StaticMethod_ReturnsNull()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", isStatic: true, parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: false);
        Assert.Null(plan.SwiftSelf);
    }

    [Fact]
    public void SwiftSelf_AsyncMethod_ReturnsNull()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "fetch", parentKind: ParentKind.Class, isAsync: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig,
            requiresSwiftSelf: true, requiresSwiftAsync: true);
        Assert.Null(plan.SwiftSelf);
    }

    [Fact]
    public void SwiftSelf_ClassInstance_ReturnsClassKind()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: true);

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.Class, plan.SwiftSelf!.Kind);
        Assert.Contains("(void*)_handle.DangerousGetHandle()", plan.SwiftSelf.CreationCode);
    }

    [Fact]
    public void SwiftSelf_NonFrozenStruct_ReturnsNonFrozenKind()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "update", parentKind: ParentKind.NonFrozenStruct);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: true);

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.NonFrozenStruct, plan.SwiftSelf!.Kind);
        Assert.Contains("(void*)_payload", plan.SwiftSelf.CreationCode);
    }

    [Fact]
    public void SwiftSelf_FrozenStruct_ReturnsFrozenValueKind()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "getValue", parentKind: ParentKind.FrozenStruct);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: true);

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.FrozenStructValue, plan.SwiftSelf!.Kind);
        Assert.Contains("SwiftSelf<", plan.SwiftSelf.CreationCode);
        Assert.Contains("(this)", plan.SwiftSelf.CreationCode);
    }

    [Fact]
    public void SwiftSelf_FixedBlock_ReturnsFixedBlockKind()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "setValue", parentKind: ParentKind.FrozenStruct);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig,
            requiresSwiftSelf: true, requiresFixedBlock: true);

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.FixedBlock, plan.SwiftSelf!.Kind);
        Assert.Contains("__self", plan.SwiftSelf.CreationCode);
    }

    [Fact]
    public void SwiftSelf_FrozenStructWithMemory_ReturnsBufferKind()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "getUrl", parentKind: ParentKind.FrozenStructWithMemory);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: true);

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.FrozenStructBuffer, plan.SwiftSelf!.Kind);
        Assert.Contains(".Buffer", plan.SwiftSelf.CreationCode);
    }

    #endregion

    #region SwiftError Tests

    [Fact]
    public void SwiftError_NonThrowing_ReturnsNull()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftError: false);
        Assert.Null(plan.SwiftError);
    }

    [Fact]
    public void SwiftError_UntypedThrows_ContainsSwiftRuntimeException()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "parse", parentKind: ParentKind.Class, throws: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftError: true);

        Assert.NotNull(plan.SwiftError);
        Assert.False(plan.SwiftError!.IsTypedThrows);
        Assert.Contains("swiftError.Value != null", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_GetErrorDescription", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_ReleaseError", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_Free", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SwiftRuntimeException", plan.SwiftError.ErrorCheckCode);
    }

    [Fact]
    public void SwiftError_TypedThrows_ContainsSwiftException()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "parse", parentKind: ParentKind.Class, throws: true, hasTypedThrows: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftError: true);

        Assert.NotNull(plan.SwiftError);
        Assert.True(plan.SwiftError!.IsTypedThrows);
        Assert.Contains("swiftError.Value != null", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_GetErrorDescription", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_ReleaseError", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SwiftException<", plan.SwiftError.ErrorCheckCode);
        Assert.Equal("TestModule.ParseError", plan.SwiftError.TypedErrorTypeName);

        // C2: New fields for typed error extraction
        Assert.Equal("TestModule.ParseError", plan.SwiftError.SwiftErrorTypeName);
        Assert.Equal("TestModule_ParseError", plan.SwiftError.TypedErrorSafeSuffix);
        Assert.Contains("SBW_ExtractTypedError_TestModule_ParseError", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("MarshalFromSwift<TestModule.ParseError>", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_Free(_typedErrorPtr)", plan.SwiftError.ErrorCheckCode);
    }

    #endregion

    #region IndirectResult Tests

    [Fact]
    public void IndirectResult_NotRequired_BothNull()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: false);

        Assert.Null(plan.IndirectResultConstructor);
        Assert.Null(plan.IndirectResultMethod);
    }

    [Fact]
    public void IndirectResult_Constructor_ContainsSwiftSafeHandle()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "init", parentKind: ParentKind.Class, isConstructor: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultConstructor);
        Assert.True(plan.IndirectResultConstructor!.IsConstructor);
        Assert.Contains("SwiftSafeHandle", plan.IndirectResultConstructor.AllocationCode);
        Assert.Contains("SwiftIndirectResult", plan.IndirectResultConstructor.AllocationCode);
    }

    [Fact]
    public void IndirectResult_Method_ContainsTypeMetadata()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("TestModule.Widget"));
        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Loader");
        RegisterType(testModule, "TestModule.Widget", "TestModule", "Widget",
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        var env2 = new MethodEnvironment(env.MethodDecl, typeDb);
        var plan = BuildPlan(env2, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.False(plan.IndirectResultMethod!.IsConstructor);
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow", plan.IndirectResultMethod.AllocationCode);
        Assert.Contains("NativeMemory.Alloc", plan.IndirectResultMethod.AllocationCode);
    }

    [Fact]
    public void IndirectResult_CdeclNonFrozenStructReturn_CleanupCodeIsNull()
    {
        // Bug 1: @_cdecl property getter returning non-frozen struct must NOT free the payload
        // buffer. NewFromPayload takes ownership of the buffer pointer — freeing it causes
        // use-after-free. CleanupCode must be null so no NativeMemory.Free is emitted.
        var moduleDecl = CreateModuleDecl();
        var structDecl = new StructDecl
        {
            Name = "Config",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            MangledName = "$s10TestModule6ConfigVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule6ConfigVMa"
        };
        moduleDecl.Types.Add(structDecl);

        var method = new MethodDecl
        {
            Name = "config_Get",
            MangledName = "SBW_TestModule_Loader_config_Get",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclPropertyWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Config"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("Loader", moduleDecl),
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Loader", structName: "Config", frozen: false);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.Config", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Null(plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclComplexEnumReturn_CleanupCodeIsNull()
    {
        // Bug 1: @_cdecl method returning complex enum must NOT free the payload buffer.
        // NewFromPayload takes ownership — same as non-frozen struct.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Parser", moduleDecl);
        var method = new MethodDecl
        {
            Name = "parse",
            MangledName = "SBW_TestModule_Parser_parse",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Result"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Parser");
        RegisterType(testModule, "TestModule.Result", "TestModule", "Result",
            TypeRecordFlags.None, TypeRecordKind.Enum);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.Result", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Null(plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclFrozenStructReturn_CleanupCodeIsNotNull()
    {
        // Bug 1 inverse: @_cdecl method returning frozen struct MUST free the buffer.
        // Frozen structs are copied out by value — the temp buffer must be freed.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Fetcher", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getPoint",
            MangledName = "SBW_TestModule_Fetcher_getPoint",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Point"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Fetcher", structName: "Point", frozen: true);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.Point", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Equal("NativeMemory.Free(payload);", plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclUtf8SliceReturn_CleanupCodeIsNotNull()
    {
        // Bug 1 inverse: @_cdecl method returning Utf8Slice MUST free the buffer.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Fetcher", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getName",
            MangledName = "SBW_TestModule_Fetcher_getName",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.String"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Fetcher");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("Utf8Slice", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Equal("NativeMemory.Free(payload);", plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclFrozenBlittableReturn_UsesUnsafeSizeOf()
    {
        // Bug 3: @_cdecl method returning frozen blittable struct (like CGSize) must use
        // Unsafe.SizeOf instead of TypeMetadata.GetTypeMetadataOrThrow. Frozen blittable
        // structs are plain C# value types with no ISwiftObject implementation.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Canvas", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getSize",
            MangledName = "SBW_TestModule_Canvas_getSize",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("CoreFoundation.CGSize"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int", "System", "Int64", "$sSiMa");
        typeDb.AddModuleDatabase(swiftModule);
        // Register CGSize in a CoreFoundation module (frozen blittable, no RequiresMemoryManagement)
        var cfModule = new ModuleTypeDatabase("CoreFoundation", "/usr/lib/swift/libswiftCoreFoundation.dylib");
        RegisterType(cfModule, "CoreFoundation.CGSize", "Swift", "CGSize",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);
        typeDb.AddModuleDatabase(cfModule);
        var canvasModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        canvasModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Canvas"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(canvasModule);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("Swift.CGSize", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Contains("Unsafe.SizeOf<Swift.CGSize>", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("TypeMetadata.GetTypeMetadataOrThrow", plan.IndirectResultMethod.AllocationCode);
        Assert.Equal("NativeMemory.Free(payload);", plan.IndirectResultMethod.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclFrozenWithMemoryReturn_UsesTypeMetadata()
    {
        // Bug 3 inverse: Frozen struct WITH RequiresMemoryManagement (e.g., URL) must still
        // use TypeMetadata — it's not a plain blittable struct.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Fetcher", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getUrl",
            MangledName = "SBW_TestModule_Fetcher_getUrl",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.UrlWrapper"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Fetcher", structName: "UrlWrapper",
            frozen: true, requiresMemoryManagement: true);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.UrlWrapper", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("Unsafe.SizeOf", plan.IndirectResultMethod.AllocationCode);
    }

    #endregion

    #region OptionalReturnBuffer Tests

    [Fact]
    public void OptionalReturnBuffer_NonOptionalReturn_Null()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "getValue", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("Swift.Int"));
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Null(plan.OptionalReturnBuffer);
    }

    [Fact]
    public void OptionalReturnBuffer_LargeOptionalReturn_ContainsStackalloc()
    {
        // Optional<Swift.Int> is a "large optional" — triggers the return buffer
        var optionalReturnType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "findValue", parentKind: ParentKind.Class,
            returnType: optionalReturnType);
        env.MethodDecl.HasOptionalPointerWrapper = true;
        // Rebuild plan with the updated method
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.NotNull(plan.OptionalReturnBuffer);
        Assert.Contains("stackalloc", plan.OptionalReturnBuffer!.AllocationCode);
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow", plan.OptionalReturnBuffer.AllocationCode);
    }

    [Fact]
    public void OptionalReturnBuffer_AsyncMethod_Null()
    {
        // Async methods excluded from optional return buffer
        var optionalReturnType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "findValueAsync", parentKind: ParentKind.Class,
            returnType: optionalReturnType, isAsync: true);
        env.MethodDecl.HasOptionalPointerWrapper = true;
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Null(plan.OptionalReturnBuffer);
    }

    #endregion

    #region DeclarationLines Tests

    [Fact]
    public void DeclarationLines_NoGenericsNoClosures_Empty()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Empty(plan.DeclarationLines);
    }

    [Fact]
    public void DeclarationLines_GenericParam_ContainsMetadataAndPayload()
    {
        var (env, wrapperSig, pInvokeSig) = CreateGenericMethodSetup();
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.True(plan.DeclarationLines.Count >= 2);
        Assert.Contains(plan.DeclarationLines, l => l.Contains("TypeMetadata"));
        Assert.Contains(plan.DeclarationLines, l => l.Contains("IntPtr") && l.Contains("IntPtr.Zero"));
    }

    #endregion

    #region PInvokeCall Tests

    [Fact]
    public void PInvokeCall_VoidReturn_NoResultPrefix()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("doWork", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.DoesNotContain("var result = ", plan.PInvokeCallStatement);
    }

    [Fact]
    public void PInvokeCall_NonVoidReturn_HasResultPrefix()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("Swift.Int"));
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.Contains("var result = ", plan.PInvokeCallStatement);
    }

    [Fact]
    public void PInvokeCall_IndirectResult_NoResultPrefix()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("Swift.Int"));
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.DoesNotContain("var result = ", plan.PInvokeCallStatement);
    }

    #endregion

    #region GenericArgumentMarshalling Tests

    [Fact]
    public void GenericArgMarshalling_NoGenerics_Empty()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Empty(plan.GenericArgumentMarshallingLines);
    }

    [Fact]
    public void GenericArgMarshalling_HasGeneric_ContainsStackallocAndMarshal()
    {
        var (env, wrapperSig, pInvokeSig) = CreateGenericMethodSetup();
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.True(plan.GenericArgumentMarshallingLines.Count >= 3);
        Assert.Contains(plan.GenericArgumentMarshallingLines, l => l.Contains("stackalloc"));
        Assert.Contains(plan.GenericArgumentMarshallingLines, l => l.Contains("MarshalToSwift"));
    }

    #endregion

    #region WitnessTable Tests

    [Fact]
    public void WitnessTable_NoGenerics_Empty()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Empty(plan.WitnessTableStatements);
    }

    #endregion

    #region GenericInoutWriteback Tests

    [Fact]
    public void InoutWriteback_NoInout_Empty()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.Empty(plan.GenericInoutWritebackLines);
    }

    #endregion

    #region FixedBlock Tests

    [Fact]
    public void FixedBlock_NotRequired_NullHeader()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup("fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresFixedBlock: false);
        Assert.Null(plan.FixedBlockHeader);

    }

    [Fact]
    public void FixedBlock_Required_ContainsFixedKeyword()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "setValue", parentKind: ParentKind.FrozenStruct);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresFixedBlock: true);

        Assert.NotNull(plan.FixedBlockHeader);
        Assert.Contains("fixed (", plan.FixedBlockHeader!);
        Assert.Contains("__self", plan.FixedBlockHeader);

    }

    #endregion

    #region RequiresUnsafe Tests

    [Fact]
    public void RequiresUnsafe_Constructor_AlwaysTrue()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "init", parentKind: ParentKind.Class, isConstructor: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);
        Assert.True(plan.RequiresUnsafe);
    }

    [Fact]
    public void RequiresUnsafe_StaticVoidMethod_False()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "doWork", isStatic: true, parentKind: ParentKind.Module);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig,
            requiresSwiftSelf: false, requiresSwiftError: false);
        Assert.False(plan.RequiresUnsafe);
    }

    [Fact]
    public void RequiresUnsafe_InstanceMethod_True()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "fetch", parentKind: ParentKind.Class);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftSelf: true);
        Assert.True(plan.RequiresUnsafe);
    }

    [Fact]
    public void RequiresUnsafe_ThrowingMethod_True()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "parse", isStatic: true, parentKind: ParentKind.Module, throws: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig,
            requiresSwiftSelf: false, requiresSwiftError: true);
        Assert.True(plan.RequiresUnsafe);
    }

    #endregion

    #region Test Helpers

    private enum ParentKind
    {
        Module,
        Class,
        FrozenStruct,
        FrozenStructWithMemory,
        NonFrozenStruct
    }

    private static (MethodEnvironment env, Signature wrapperSig, Signature pInvokeSig)
        CreateMethodSetup(
            string name,
            ParentKind parentKind = ParentKind.Module,
            bool isStatic = false,
            bool isConstructor = false,
            bool isAsync = false,
            bool throws = false,
            bool hasTypedThrows = false,
            TypeSpec? returnType = null)
    {
        var moduleDecl = CreateModuleDecl();
        BaseDecl parentDecl;
        TypeDatabase typeDb;
        ModuleTypeDatabase testModule;

        switch (parentKind)
        {
            case ParentKind.Class:
                parentDecl = CreateClassDecl("Loader", moduleDecl);
                (typeDb, testModule) = CreateTypeDatabaseWithModule("Loader");
                break;
            case ParentKind.FrozenStruct:
                parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
                (typeDb, testModule) = CreateTypeDatabaseWithModule(structName: "Point", frozen: true);
                break;
            case ParentKind.FrozenStructWithMemory:
                parentDecl = CreateFrozenStructDecl("UrlWrapper", moduleDecl);
                (typeDb, testModule) = CreateTypeDatabaseWithModule(structName: "UrlWrapper", frozen: true, requiresMemoryManagement: true);
                break;
            case ParentKind.NonFrozenStruct:
                var structDecl = new StructDecl
                {
                    Name = "Config",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                    MangledName = "$s10TestModule6ConfigVN",
                    Properties = new List<PropertyDecl>(),
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    Conformances = new List<TypeConformance>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl,
                    IsFrozen = false,
                    MetadataAccessor = "$s10TestModule6ConfigVMa"
                };
                moduleDecl.Types.Add(structDecl);
                parentDecl = structDecl;
                (typeDb, testModule) = CreateTypeDatabaseWithModule(structName: "Config", frozen: false);
                break;
            default:
                parentDecl = moduleDecl;
                (typeDb, testModule) = CreateTypeDatabaseWithModule();
                break;
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}SiyF",
            MethodType = isStatic || parentKind == ParentKind.Module ? MethodType.Static : MethodType.Instance,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", returnType ?? TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        if (hasTypedThrows)
        {
            method.ThrownErrorType = new NamedTypeSpec("TestModule.ParseError");
            RegisterType(testModule, "TestModule.ParseError", "TestModule", "ParseError",
                TypeRecordFlags.Frozen, TypeRecordKind.Struct);
        }

        if (parentDecl is TypeDecl td)
            td.Methods.Add(method);
        else if (parentDecl is ModuleDecl md)
            md.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);

        var wrapperRetType = returnType != null ? "long" : "void";
        var pInvokeRetType = returnType != null ? "Int64" : "void";
        var wrapperSig = new Signature(wrapperRetType, Array.Empty<Parameter>());
        var pInvokeSig = new Signature(pInvokeRetType, Array.Empty<Parameter>());

        return (env, wrapperSig, pInvokeSig);
    }

    private static (MethodEnvironment env, Signature wrapperSig, Signature pInvokeSig)
        CreateGenericMethodSetup()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Container", moduleDecl);
        var typeDb = CreateTypeDatabase("Container");

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9Container7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                new ArgumentDecl
                {
                    Name = "item",
                    PrivateName = "_item",
                    SwiftTypeSpec = new NamedTypeSpec("T"),
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "T",
                    "T",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        var wrapperSig = new Signature("void", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());

        return (env, wrapperSig, pInvokeSig);
    }

    private static SyncMethodPlan BuildPlan(
        MethodEnvironment env,
        Signature wrapperSig,
        Signature pInvokeSig,
        bool requiresIndirectResult = false,
        bool requiresSwiftSelf = false,
        bool requiresSwiftError = false,
        bool requiresSwiftAsync = false,
        bool requiresFixedBlock = false)
    {
        var genericContext = env.ParentDecl is TypeDecl parentType
            ? GenericContext.FromMethodInType(env.MethodDecl, parentType)
            : GenericContext.FromMethod(env.MethodDecl);

        var builder = new MethodMarshalPlanBuilder(
            env, genericContext, wrapperSig, pInvokeSig,
            requiresIndirectResult, requiresSwiftSelf, requiresSwiftError,
            requiresSwiftAsync, requiresFixedBlock,
            protocolTypeName =>
            {
                if (env.TypeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
                    return record.Kind == TypeRecordKind.Protocol &&
                           !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
                return false;
            });

        return builder.BuildSyncPlan();
    }

    [Fact]
    public void PInvokeCall_CdeclGenericClassConstructor_UsesClassMetadata()
    {
        // @_cdecl constructor wrappers on generic classes must pass the class's own metadata
        // (e.g., GenericCache<T>.GetTypeMetadata()) as _metadata0, NOT per-param metadata
        // (SwiftObjectHelper<T>.GetTypeMetadata()). The Swift wrapper does
        // unsafeBitCast(_metadata0, to: Any.Type.self) for protocol metatype dispatch.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("GenericCache", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var typeDb = CreateTypeDatabase("GenericCache");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "SBW_TestModule_GenericCache_init_abc12345",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            UsesCdeclConstructorWrapper = true,
            UsesWrapperLibrary = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("capacity", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var helperContext = PInvokeHelperContext.CreateIfGeneric(classDecl);
        Assert.NotNull(helperContext);
        var env = new MethodEnvironment(method, typeDb, pinvokeHelperContext: helperContext);

        var wrapperSig = new Signature("void", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        // Must use SwiftObjectHelper<GenericCache<T>>.GetTypeMetadata() for _metadata0
        // (routes through ISwiftObject to get specialized class metatype)
        Assert.Contains("SwiftObjectHelper<GenericCache<T>>.GetTypeMetadata()", plan.PInvokeCallStatement);
        // Must NOT use per-param SwiftObjectHelper<T>.GetTypeMetadata() (wrong metatype)
        Assert.DoesNotContain("SwiftObjectHelper<T>.GetTypeMetadata()", plan.PInvokeCallStatement);
    }

    [Fact]
    public void PInvokeCall_NonCdeclGenericClassConstructor_UsesSwiftObjectHelper()
    {
        // Non-@_cdecl constructors (silgen_name path) should still use per-param metadata
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("GenericCache", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var typeDb = CreateTypeDatabase("GenericCache");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s_silgen_init",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            UsesCdeclConstructorWrapper = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("capacity", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var helperContext = PInvokeHelperContext.CreateIfGeneric(classDecl);
        var env = new MethodEnvironment(method, typeDb, pinvokeHelperContext: helperContext);

        var wrapperSig = new Signature("void", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        // Non-@_cdecl should use per-param metadata
        Assert.Contains("SwiftObjectHelper", plan.PInvokeCallStatement);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = ""
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase(
        string? className = null,
        string? structName = null,
        bool frozen = false,
        bool requiresMemoryManagement = false)
    {
        var (typeDb, _) = CreateTypeDatabaseWithModule(className, structName, frozen, requiresMemoryManagement);
        return typeDb;
    }

    private static (TypeDatabase typeDb, ModuleTypeDatabase testModule) CreateTypeDatabaseWithModule(
        string? className = null,
        string? structName = null,
        bool frozen = false,
        bool requiresMemoryManagement = false)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int", "System", "Int64", "$sSiMa");
        RegisterPrimitive(swiftModule, "Swift.String", "Swift", "SwiftString", "$sSSMa",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        if (className != null)
        {
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", className),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
                    MetadataAccessor = "$sMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                });
        }

        if (structName != null)
        {
            var flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.RequiresMemoryManagement;
            if (requiresMemoryManagement)
                flags |= TypeRecordFlags.RequiresMemoryManagement;
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"TestModule.{structName}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", structName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{structName}"),
                    MetadataAccessor = "$sMa",
                    Flags = flags,
                    Kind = TypeRecordKind.Struct
                });
        }

        typeDb.AddModuleDatabase(testModule);
        return (typeDb, testModule);
    }

    private static void RegisterPrimitive(ModuleTypeDatabase module, string swiftName, string csNamespace, string csName, string accessor,
        TypeRecordFlags flags = TypeRecordFlags.Frozen)
    {
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = accessor,
                Flags = flags,
                Kind = TypeRecordKind.Struct
            });
    }

    private static void RegisterType(ModuleTypeDatabase module, string swiftName, string csNamespace, string csName,
        TypeRecordFlags flags, TypeRecordKind kind)
    {
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = kind
            });
    }

    #endregion
}
