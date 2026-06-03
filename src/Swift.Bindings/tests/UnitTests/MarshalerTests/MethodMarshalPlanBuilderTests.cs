// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Globalization;
using System.Linq;
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
    public void SwiftError_UntypedThrows_ContainsSwiftException()
    {
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "parse", parentKind: ParentKind.Class, throws: true);
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresSwiftError: true);

        Assert.NotNull(plan.SwiftError);
        Assert.False(plan.SwiftError!.IsTypedThrows);
        Assert.Contains("swiftError.Value != null", plan.SwiftError.ErrorCheckCode);
        // Untyped throws now delegates to SwiftMarshal.ThrowSwiftError (consolidates description read + release + throw)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_GetErrorDescription", plan.SwiftError.ErrorCheckCode);
        Assert.Contains("SBW_ReleaseError", plan.SwiftError.ErrorCheckCode);
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
        Assert.Equal("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclBufferVariable_DoesNotCollideWithPayloadParam()
    {
        // Regression test: @_cdecl result buffer variable was named "payload" which caused
        // CS0136 when a method parameter was also named "payload" (e.g., Starscream.WSFramer.createWriteFrame).
        // The variable is now named "_cdeclBuf" to avoid collision.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Framer", moduleDecl);
        var method = new MethodDecl
        {
            Name = "createWriteFrame",
            MangledName = "SBW_TestModule_Framer_createWriteFrame",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Point"), moduleDecl),  // return type
                CreateArg("payload", new NamedTypeSpec("Swift.String"), moduleDecl)  // param named "payload"
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Framer", structName: "Point", frozen: true);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.Point", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        // Buffer variable must NOT be named "payload" — it would collide with the method parameter
        Assert.DoesNotContain("payload", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("payload", plan.IndirectResultMethod.CleanupCode ?? "");
        Assert.Contains("_cdeclBuf", plan.IndirectResultMethod.AllocationCode);
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
        Assert.Equal("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclStringReturn_UsesUtf8SliceAllocation()
    {
        // String return via @_cdecl wrapper: the Swift wrapper converts the String to
        // SBW_Utf8Slice (ptr + len), so C# must allocate fixed 2-pointer size.
        // Using the projected C# type "string" with TypeMetadata.GetTypeMetadataOrThrow
        // crashes at runtime because C# string has no Swift type metadata.
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

        // Wrapper signature says "string" (the projected C# type), NOT "Utf8Slice"
        var wrapperSig = new Signature("string", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        // Must use fixed Utf8Slice allocation, not TypeMetadata.GetTypeMetadataOrThrow<string>()
        Assert.Contains("nint.Size * 2", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("TypeMetadata.GetTypeMetadataOrThrow", plan.IndirectResultMethod.AllocationCode);
        Assert.Equal("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclArrayReturn_UsesSwiftArrayAllocation()
    {
        // Array return via @_cdecl wrapper: the Swift wrapper writes the full Swift.Array
        // value to the result buffer. C# must allocate using SwiftArray<T> metadata, not
        // the projected IReadOnlyList<T> which has no Swift type metadata.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Store", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getKeys",
            MangledName = "SBW_TestModule_Store_getKeys",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String")), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Store");
        var env = new MethodEnvironment(method, typeDb);

        // Wrapper signature says "IReadOnlyList<string>" (the projected C# type)
        var wrapperSig = new Signature("IReadOnlyList<string>", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        // Must use SwiftArray metadata, not IReadOnlyList<string>
        Assert.Contains("SwiftArray<SwiftString>", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("IReadOnlyList", plan.IndirectResultMethod.AllocationCode);
        // Copy-out carrier: the from-handle ctor InitializeWithCopy's the wire buffer into a
        // fresh SafeHandle-owned buffer, so the temp must be value-witness Destroyed (with the
        // SwiftArray wire metadata) before free, or the source's +1 is orphaned every call.
        // Resolve via the cached TryGetTypeMetadata<wireType> and destroy through the non-generic
        // overload — never the generic DestroyWireBufferRetains<wireType> (a new generic
        // instantiation in a generic wrapper's finally crashes Mono JIT, jit-info.c:918).
        Assert.Contains("TryGetTypeMetadata<SwiftArray<SwiftString>>", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("DestroyWireBufferRetains((IntPtr)_cdeclBuf,", plan.IndirectResultMethod.CleanupCode);
        Assert.DoesNotContain("DestroyWireBufferRetains<", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod.CleanupCode);
    }

    [Fact]
    public void IndirectResult_CdeclDictionaryReturn_UsesSwiftDictionaryAllocation()
    {
        // Dictionary return via @_cdecl wrapper: allocation must use SwiftDictionary<K,V>,
        // not the projected IReadOnlyDictionary<K,V>.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Config", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getAll",
            MangledName = "SBW_TestModule_Config_getAll",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Dictionary",
                    new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.String")), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Config");
        var env = new MethodEnvironment(method, typeDb);

        // Wrapper signature says "IReadOnlyDictionary<string, string>" (projected type)
        var wrapperSig = new Signature("IReadOnlyDictionary<string, string>", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        // Must use SwiftDictionary metadata, not IReadOnlyDictionary
        Assert.Contains("SwiftDictionary<SwiftString, SwiftString>", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("IReadOnlyDictionary", plan.IndirectResultMethod.AllocationCode);
        // Copy-out carrier: Destroy the temp wire buffer (with the SwiftDictionary wire metadata)
        // before free so the source's +1 retain isn't orphaned every call. Resolve via the cached
        // TryGetTypeMetadata<wireType> + non-generic destroy overload (not the generic form).
        Assert.Contains("TryGetTypeMetadata<SwiftDictionary<SwiftString, SwiftString>>", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("DestroyWireBufferRetains((IntPtr)_cdeclBuf,", plan.IndirectResultMethod.CleanupCode);
        Assert.DoesNotContain("DestroyWireBufferRetains<", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod.CleanupCode);
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
        Assert.Equal("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod.CleanupCode);
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
        // Must use real type name (UrlWrapper), NOT the .Buffer ABI struct.
        // FrozenWithMemoryProjection.ContainerTypeName returns "Foo.Buffer" but
        // MarshalFromSwiftType returns the real type — allocation must use the real type.
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow<TestModule.UrlWrapper>", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain(".Buffer", plan.IndirectResultMethod.AllocationCode);
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

    [Fact]
    public void DeclarationLines_NonAsyncClosureParam_DeclaresGCHandle()
    {
        // Baseline for the closure declaration gate: a plain `(Int32) -> Void`
        // parameter takes the legacy thunked GCHandle path. The pre-declaration
        // is required so the finally block can free the handle.
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);
        var plan = BuildClosureParamPlan("acceptCallback", closureSpec, "callback");

        Assert.Contains("GCHandle callbackHandle = default;", plan.DeclarationLines);
    }

    [Fact]
    public void DeclarationLines_NonBaselineAsyncThrowingClosureParam_DeclaresGCHandle()
    {
        // Stripe ConfirmHandler-shape closure: `(Int32, Bool) async throws -> String`.
        // Bool is outside GetAsyncThrowingArgCategory's blittable-primitive set, so
        // IsBaselineAsyncClosure returns false and WrapperEmitter.Marshalling falls
        // to the legacy GCHandle path that emits `valueHandle = GCHandle.Alloc(value)`.
        // The plan builder must pair that assignment with a pre-try declaration —
        // otherwise the generated C# fails to compile (CS0103 undeclared identifier).
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("Swift.Int32"),
                new NamedTypeSpec("Swift.Bool"),
            }),
            new NamedTypeSpec("Swift.String"))
        {
            IsAsync = true,
            Throws = true,
        };
        var plan = BuildClosureParamPlan("setConfirmHandler", closureSpec, "handler");

        Assert.Contains("GCHandle handlerHandle = default;", plan.DeclarationLines);
    }

    [Fact]
    public void DeclarationLines_BaselineAsyncThrowingClosureParam_SuppressesGCHandle()
    {
        // Baseline-shape async throwing closure `(Int32) async throws -> Int32`.
        // EmitAsyncThrowingClosureMarshallingSetup synthesizes the handle internally
        // (`var handlerHandle = GCHandle.Alloc(...)`), so a pre-try declaration here
        // would duplicate the symbol. The gate must suppress it.
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Int32"))
        {
            IsAsync = true,
            Throws = true,
        };
        var plan = BuildClosureParamPlan("setHandler", closureSpec, "handler");

        Assert.DoesNotContain(plan.DeclarationLines, l => l.Contains("GCHandle handlerHandle"));
    }

    [Fact]
    public void DeclarationLines_BaselineAsyncNonThrowingClosureParam_SuppressesGCHandle()
    {
        // Baseline-shape async non-throwing closure `() async -> Int32` — same gate
        // as the throwing baseline: the bridge declares its own handle, so the
        // pre-try declaration must be suppressed.
        var closureSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int32"))
        {
            IsAsync = true,
            Throws = false,
        };
        var plan = BuildClosureParamPlan("setFactory", closureSpec, "factory");

        Assert.DoesNotContain(plan.DeclarationLines, l => l.Contains("GCHandle factoryHandle"));
    }

    private static SyncMethodPlan BuildClosureParamPlan(
        string methodName, ClosureTypeSpec closureSpec, string paramName)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Holder", moduleDecl);
        var method = new MethodDecl
        {
            Name = methodName,
            // Mangled name without 'XC' so HasConventionCInMangledName returns false
            // and the closure routes through the normal thunked-closure path.
            MangledName = $"$s10TestModule6Holder{methodName.Length}{methodName}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg(paramName, closureSpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
        };
        classDecl.Methods.Add(method);

        var typeDb = CreateTypeDatabaseWithClosurePrimitives("Holder");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("void", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        return BuildPlan(env, wrapperSig, pInvokeSig);
    }

    private static TypeDatabase CreateTypeDatabaseWithClosurePrimitives(string className)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int", "System", "Int64", "$sSiMa");
        RegisterPrimitive(swiftModule, "Swift.Int32", "System", "Int32", "$ss5Int32VMa");
        RegisterPrimitive(swiftModule, "Swift.Bool", "System", "Boolean", "$sSbMa");
        RegisterPrimitive(swiftModule, "Swift.String", "Swift", "SwiftString", "$sSSMa",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", className),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            });
        typeDb.AddModuleDatabase(testModule);
        return typeDb;
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

    [Fact]
    public void PInvokeCall_ReturnLocal_DoesNotCollideWithResultParam()
    {
        // Regression test: P/Invoke return local was hardcoded to "result" which caused
        // CS0841/CS0136 self-referential shadowing when a method parameter was also named
        // "result" (e.g., a Swift method like `func write(result: Int) -> Int`).
        // The local is now renamed to "__result" when the collision is detected.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = new MethodDecl
        {
            Name = "compute",
            MangledName = "$s10TestModule6Loader7computeySi6resultSi_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Int"), moduleDecl),       // return type
                CreateArg("result", new NamedTypeSpec("Swift.Int"), moduleDecl)  // param named "result"
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var (typeDb, _) = CreateTypeDatabaseWithModule("Loader");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("long", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("Int64", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        // The hardcoded "var result = " would shadow the parameter — must be renamed.
        Assert.DoesNotContain("var result = ", plan.PInvokeCallStatement);
        Assert.Contains("var __result = ", plan.PInvokeCallStatement);
        Assert.Equal("__result", plan.ReturnLocalName);
    }

    [Fact]
    public void PInvokeCall_ReturnLocal_NoCollision_StaysAsResult()
    {
        // Sanity: when no parameter is named "result", the return local stays as "result"
        // to avoid churning generated output for the common case.
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("Swift.Int"));
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.Contains("var result = ", plan.PInvokeCallStatement);
        Assert.Equal("result", plan.ReturnLocalName);
    }

    [Fact]
    public void PInvokeCall_ReturnLocal_SkipsOver__resultParam()
    {
        // Iterative resolver: if both "result" and "__result" are taken by parameters,
        // pick "__result1" so the rename itself doesn't reintroduce a collision.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = new MethodDecl
        {
            Name = "compute",
            MangledName = "$s10TestModule6Loader7computeySi6resultSi__resultSi_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("result", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("__result", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var (typeDb, _) = CreateTypeDatabaseWithModule("Loader");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("long", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("Int64", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig);

        Assert.Equal("__result1", plan.ReturnLocalName);
        Assert.Contains("var __result1 = ", plan.PInvokeCallStatement);
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

    [Fact]
    public void WitnessTable_MultipleConformances_OrderedOrdinallyNotByCulture()
    {
        // PWT slot order must be deterministic (ordinal) so it matches the P/Invoke signature
        // (PInvokeEmitter) and the witness-table accessor (PInvokeHelperEmitter), which both sort
        // the same conformances with StringComparer.Ordinal. Ordinal sorts uppercase 'Z' (90)
        // before lowercase 'a' (97); the en-US culture's default comparison sorts "apple" before
        // "Zebra". Under a non-ordinal current culture the previous culture-sensitive OrderBy
        // emitted the witness tables in [apple, Zebra] order — a slot mismatch versus the P/Invoke
        // signature that passes the wrong PWT for a conformance.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Container", moduleDecl);

        var zebra = new GenericParameterConformance(
            new[] { "T" },
            SwiftTypeName.FromModuleQualifiedName("TestModule.Zebra"),
            ConformanceKind.Protocol);
        var apple = new GenericParameterConformance(
            new[] { "T" },
            SwiftTypeName.FromModuleQualifiedName("TestModule.apple"),
            ConformanceKind.Protocol);

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
            // Deliberately add the conformances in ordinal order; the builder must re-sort
            // by ordinal regardless of insertion order or current culture.
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "T", "T",
                    new List<GenericParameterConformance> { zebra, apple },
                    new List<GenericParameterConformance>())
            },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Container");
        RegisterType(testModule, "TestModule.Zebra", "TestModule", "Zebra",
            TypeRecordFlags.None, TypeRecordKind.Protocol);
        RegisterType(testModule, "TestModule.apple", "TestModule", "apple",
            TypeRecordFlags.None, TypeRecordKind.Protocol);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("void", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());

        var previousCulture = CultureInfo.CurrentCulture;
        SyncMethodPlan plan;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            plan = BuildPlan(env, wrapperSig, pInvokeSig);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        var pwtLines = plan.WitnessTableStatements
            .Where(l => l.Contains("GetOrThrow"))
            .ToList();
        Assert.Equal(2, pwtLines.Count);

        var zebraIndex = pwtLines.FindIndex(l => l.Contains("Zebra"));
        var appleIndex = pwtLines.FindIndex(l => l.Contains("apple"));
        Assert.True(zebraIndex >= 0 && appleIndex >= 0,
            $"both PWT lines must be present; got: {string.Join(" | ", pwtLines)}");
        Assert.True(zebraIndex < appleIndex,
            $"Zebra (ordinal-first) must precede apple; got: {string.Join(" | ", pwtLines)}");
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
    public void PInvokeCall_CdeclGenericClassConstructor_NoExtraMetatypeArg()
    {
        // @_cdecl constructor wrappers on generic classes handle metatype dispatch internally.
        // The metadata is passed via HandleGenericMetadata() as a regular P/Invoke parameter
        // (maps to _metadata0 in the @_cdecl wrapper). No extra trailing metatype argument
        // (PInvoke_getMetadata) should be appended — that would cause a parameter count mismatch
        // between the C# call site and the @_cdecl wrapper's parameter list.
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

        // @_cdecl constructor: no extra metatype argument (PInvoke_getMetadata) should be appended.
        // The helper class P/Invoke call should NOT contain the metatype accessor.
        Assert.DoesNotContain("PInvoke_getMetadata", plan.PInvokeCallStatement);
    }

    [Fact]
    public void PInvokeCall_NonCdeclGenericClassConstructor_UsesUnconstrainedTypeMetadataAccessor()
    {
        // Non-@_cdecl constructors (silgen_name path) should still use per-param metadata.
        // The metadata source is the unconstrained TypeMetadata.GetTypeMetadataOrThrow<T>()
        // (not SwiftObjectHelper<T>), so call sites compile when the surrounding generic's
        // where clause has dropped the ISwiftObject seed for blittable instantiations.
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

        // Non-@_cdecl should use per-param metadata. Source is the unconstrained helper
        // so the call site compiles when the type's where clause has no ISwiftObject seed.
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow<T>", plan.PInvokeCallStatement);
        Assert.DoesNotContain("SwiftObjectHelper<T>", plan.PInvokeCallStatement);
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

    [Fact]
    public void IndirectResult_CdeclTupleReturn_UsesGetTupleTypeMetadataFromElements()
    {
        // Tuple returns through @_cdecl must use GetTupleTypeMetadataFromElements()
        // instead of GetTypeMetadataOrThrow<ValueTuple<...>>() because the latter
        // uses MakeGenericMethod which gets trimmed on NativeAOT.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Calculator", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = new MethodDecl
        {
            Name = "divmod",
            MangledName = "SBW_TestModule_Calculator_divmod_12345678",
            MethodType = MethodType.Static,
            IsConstructor = false,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", tupleSpec, moduleDecl),
                CreateArg("a", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("b", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        moduleDecl.Methods.Add(method);

        var (typeDb, _) = CreateTypeDatabaseWithModule("Calculator");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("ValueTuple<long, long>", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        // Must use NativeAOT-safe tuple metadata, not the generic path
        Assert.Contains("GetTupleTypeMetadataFromElements", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("GetTypeMetadataOrThrow<ValueTuple", plan.IndirectResultMethod.AllocationCode);
    }

    #endregion

    #region Non-Cdecl SwiftIndirectResult Cleanup Tests

    [Fact]
    public void IndirectResult_NonCdeclClassReturn_HasCleanupCode()
    {
        // Non-cdecl SwiftIndirectResult path: NativeMemory.Alloc must be freed.
        // Class returns copy the pointer out — the temp buffer must be freed.
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("TestModule.Widget"));
        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Loader");
        RegisterType(testModule, "TestModule.Widget", "TestModule", "Widget",
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        var env2 = new MethodEnvironment(env.MethodDecl, typeDb);
        var plan = BuildPlan(env2, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Equal("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_NonCdeclNonFrozenStructReturn_CleanupCodeIsNull()
    {
        // Non-cdecl SwiftIndirectResult for non-frozen struct: NewFromPayload takes
        // ownership of the buffer pointer — must NOT free (same as @_cdecl path).
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = new MethodDecl
        {
            Name = "getConfig",
            MangledName = "$s10TestModule6Loader9getConfigAA6ConfigVyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            // NOT a @_cdecl wrapper — uses legacy CallConvSwift with SwiftIndirectResult
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Config"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Loader", structName: "Config", frozen: false);
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("TestModule.Config", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Null(plan.IndirectResultMethod!.CleanupCode);
    }

    [Fact]
    public void IndirectResult_NonCdeclComplexEnumReturn_CleanupCodeIsNull()
    {
        // Non-cdecl SwiftIndirectResult for complex enum: NewFromPayload takes
        // ownership of the buffer pointer — must NOT free.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Parser", moduleDecl);
        var method = new MethodDecl
        {
            Name = "parse",
            MangledName = "$s10TestModule6Parser5parseAA6ResultOyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
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
    public void IndirectResult_NonCdeclBufferVariable_UsesCdeclBufNaming()
    {
        // Non-cdecl SwiftIndirectResult must use _cdeclBuf (declared before try block)
        // instead of local 'payload' to ensure accessibility in finally block for cleanup.
        var (env, wrapperSig, pInvokeSig) = CreateMethodSetup(
            "compute", parentKind: ParentKind.Class,
            returnType: new NamedTypeSpec("TestModule.Widget"));
        var (typeDb, testModule) = CreateTypeDatabaseWithModule("Loader");
        RegisterType(testModule, "TestModule.Widget", "TestModule", "Widget",
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        var env2 = new MethodEnvironment(env.MethodDecl, typeDb);
        var plan = BuildPlan(env2, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.Contains("_cdeclBuf", plan.IndirectResultMethod!.AllocationCode);
        Assert.DoesNotContain("var payload", plan.IndirectResultMethod.AllocationCode);
    }

    [Fact]
    public void IndirectResult_NonCdeclResultReturn_HasDestroyWireBufferCleanup()
    {
        // stdlib Swift.Result is a complex enum by TypeRecord, but SwiftResult.NewFromPayload
        // runs VWT InitializeWithCopy (copy-out, NOT adopt). The copy-out projection whitelist
        // must override the "complex enum adopts → null cleanup" rule: the wire buffer's +1 has
        // to be value-witness Destroyed (and the buffer freed) in the finally. Without the
        // override the cleanup is nulled, so no finally is emitted and both the source retain and
        // the temp buffer leak every call.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Factory", moduleDecl);
        var method = new MethodDecl
        {
            Name = "makeResult",
            MangledName = "$s10TestModule7Factory10makeResults6ResultOyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Result",
                    new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.String")), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (typeDb, _) = CreateTypeDatabaseWithModule("Factory");
        var env = new MethodEnvironment(method, typeDb);

        var wrapperSig = new Signature("SwiftResult<SwiftString, SwiftString>", Array.Empty<Parameter>());
        var pInvokeSig = new Signature("void", Array.Empty<Parameter>());
        var plan = BuildPlan(env, wrapperSig, pInvokeSig, requiresIndirectResult: true);

        Assert.NotNull(plan.IndirectResultMethod);
        Assert.NotNull(plan.IndirectResultMethod!.CleanupCode);
        // Result is a copy-out carrier: resolve its wire metadata via the cached
        // TryGetTypeMetadata<SwiftResult<…>> and destroy through the non-generic overload before
        // free. The generic DestroyWireBufferRetains<SwiftResult<…>> must NOT be emitted — a new
        // generic instantiation in a generic wrapper's finally crashes Mono JIT (jit-info.c:918).
        Assert.Contains("TryGetTypeMetadata<SwiftResult<", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("DestroyWireBufferRetains((IntPtr)_cdeclBuf,", plan.IndirectResultMethod.CleanupCode);
        Assert.DoesNotContain("DestroyWireBufferRetains<", plan.IndirectResultMethod.CleanupCode);
        Assert.Contains("NativeMemory.Free(_cdeclBuf);", plan.IndirectResultMethod.CleanupCode);
    }

    #endregion
}
