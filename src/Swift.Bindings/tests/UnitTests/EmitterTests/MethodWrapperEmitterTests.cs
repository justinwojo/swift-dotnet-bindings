// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MethodWrapperEmitter: per-method @_cdecl wrappers that route
/// instance/static method P/Invokes through C calling convention to avoid CallConvSwift crashes.
/// </summary>
public class MethodWrapperEmitterTests
{
    #region ShouldEmitWrapper Guard Tests

    [Fact]
    public void ShouldEmitWrapper_Constructor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_Accessor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("getter:name", parentDecl, moduleDecl);
        method.IsAccessor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericParent_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.IsAsync = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_AlreadyUsesWrapperLibrary_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.UsesWrapperLibrary = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_HasCdeclPropertyWrapper_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.UsesCdeclPropertyWrapper = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CdeclCompatibleClosureParameter_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCdeclClosureParameter_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Closure with String arg — not Cdecl-compatible
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialParam_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithParam("doWork", protocolSpec, "cache", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialReturn_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithReturn("doWork", protocolSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericContainerParam_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", arraySpec, "items", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericContainerReturn_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithReturn("doWork", arraySpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OpaqueReturn_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var opaqueReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") })
        {
            IsOpaque = true
        };
        var method = CreateMethodWithReturn("doWork", opaqueReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ActorType_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Counter", moduleDecl);
        parentDecl.IsActor = true;
        var method = CreateMethod("increment", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_SimpleInstanceMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_StaticMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ThrowingMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.Throws = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MutatingMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("mutate", parentDecl, moduleDecl);
        method.IsMutating = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Symbol Name Tests

    [Fact]
    public void GetMethodSymbolName_Format()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("Nuke", "ImagePipeline", "loadImage", "$s4Nuke13ImagePipelineC9loadImageyyF");
        Assert.StartsWith("SBW_Nuke_ImagePipeline_loadImage_", symbol);
    }

    [Fact]
    public void GetMethodSymbolName_NestedTypeDotReplaced()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("Nuke", "Outer.Inner", "doWork", "$s_mangled");
        Assert.Contains("Outer_Inner", symbol);
        Assert.DoesNotContain("Outer.Inner", symbol);
    }

    [Fact]
    public void GetMethodSymbolName_UniqueAcrossOverloads()
    {
        var sym1 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "method", "$s_mangled1");
        var sym2 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "method", "$s_mangled2");
        Assert.NotEqual(sym1, sym2);
    }

    #endregion

    #region Emission Tests

    [Fact]
    public void EmitSwiftMethodWrapper_ClassInstance_UnmanagedSelf()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_MyType_doWork_abc12345\")", output);
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StructNonMutating_LoadSelf()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getValue", new NamedTypeSpec("Swift.Int"), isClass: false);

        method.MangledName = "SBW_TestModule_MyType_getValue_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeRawPointer", output);
        Assert.Contains("self_.load(as: TestModule.MyType.self)", output);
        Assert.Contains("-> Int", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StructMutating_PointerAccess()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "mutate", TupleTypeSpec.Empty, isClass: false);

        method.IsMutating = true;
        method.MangledName = "SBW_TestModule_MyType_mutate_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StaticMethod_NoSelfParam()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "create", new NamedTypeSpec("Swift.Int"), isClass: true);
        method.MethodType = MethodType.Static;

        method.MangledName = "SBW_TestModule_MyType_create_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.create", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ThrowingMethod_ErrorOut()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);
        method.Throws = true;

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);
        Assert.Contains("do {", output);
        Assert.Contains("try obj.doWork()", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StringReturn_Utf8SlicePattern()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getName", new NamedTypeSpec("Swift.String"), isClass: true);

        method.MangledName = "SBW_TestModule_MyType_getName_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("utf8", output);
        Assert.Contains("UnsafeMutablePointer<UInt8>.allocate", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ClassReturn_PassRetained()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.OtherClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getOther", new NamedTypeSpec("TestModule.OtherClass"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getOther_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged.passRetained(", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_BoolReturn_Int8Conversion()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "isValid", new NamedTypeSpec("Swift.Bool"), isClass: true);

        method.MangledName = "SBW_TestModule_MyType_isValid_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int8", output);
        Assert.Contains("? 1 : 0", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_SimpleEnumReturn_RawValue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Status", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getStatus", new NamedTypeSpec("TestModule.Status"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getStatus_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int", output);
        Assert.Contains(".rawValue)", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_IndirectResult_WritesToResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.BigStruct", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getBigStruct", new NamedTypeSpec("TestModule.BigStruct"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getBigStruct_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("initializeMemory", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_MainActor_AnnotationPropagated()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true, isMainActorIsolated: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_Dedup_PreventsDuplicate()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        // First emission
        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var firstOutput = sw.ToString();

        // Second emission with same symbol — should be no-op
        var sw2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(sw2);
        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter2, env, ctx);
        var secondOutput = sw2.ToString();

        Assert.Contains("@_cdecl", firstOutput);
        Assert.DoesNotContain("@_cdecl", secondOutput);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_WithIntParam()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setCount", new NamedTypeSpec("Swift.Int"), "count", parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_setCount_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ count: Int", output);
        Assert.Contains("count: count", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ThrowingWithReturn_HasSentinel()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.OtherClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getOther", new NamedTypeSpec("TestModule.OtherClass"), parentDecl, moduleDecl);
        method.Throws = true;

        method.MangledName = "SBW_TestModule_MyType_getOther_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("errorOut", output);
        Assert.Contains("do {", output);
        Assert.Contains("} catch {", output);
        // Should have sentinel return in catch block for class pointer return
        Assert.Contains("UnsafeMutableRawPointer(bitPattern: 1)!", output);
    }

    #endregion

    #region UsesCdeclWrapper Computed Property

    [Fact]
    public void UsesCdeclWrapper_MethodWrapper_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "test",
            MangledName = "test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_NoWrapper_ReturnsFalse()
    {
        var method = new MethodDecl
        {
            Name = "test",
            MangledName = "test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        Assert.False(method.UsesCdeclWrapper);
    }

    #endregion

    #region Helper Methods

    private static (SwiftWriter swiftWriter, StringWriter sw, MethodDecl method, MethodEnvironment env, ModuleEmissionContext ctx) CreateMethodTestSetup(
        string methodName, TypeSpec returnType, bool isClass, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var method = CreateMethodWithReturn(methodName, returnType, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, method, env, ctx);
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithReturn(string name, TypeSpec returnType, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithParam(string name, TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
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
            MetadataAccessor = "$sMa"
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
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
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind, string? rawValueTypeName)[] extraTypes)
    {
        var typeDb = new TypeDatabase();

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        foreach (var (qualifiedName, flags, kind, rawValue) in extraTypes)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
            testModule.RegisterType(
                swiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", swiftTypeName.Name),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                    Flags = flags,
                    Kind = kind,
                    RawValueTypeName = rawValue
                });
        }

        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        return CreateTestEnvironmentWithExtraTypes(typeName,
            extraTypes.Select(t => (t.qualifiedName, t.flags, t.kind, (string?)null)).ToArray());
    }

    #endregion
}
