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
    public void ShouldEmitWrapper_GenericClassParent_ConcreteMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_MethodReferencingT_ReturnsFalse()
    {
        // Method return type references parent's generic param → can't erase
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                Name = "_result",
                PrivateName = "_result",
                IsInOut = false,
                IsGeneric = true,
                ParentDecl = null,
                ModuleDecl = null
            }
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_StaticMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("create", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
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
    public void ShouldEmitWrapper_ProtocolExistentialParam_ReturnsTrue()
    {
        // Existential params are now supported in @_cdecl wrappers via UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithParam("doWork", protocolSpec, "cache", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialReturn_ReturnsTrue()
    {
        // Existential returns are now supported in @_cdecl wrappers via indirect result pointer
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithReturn("doWork", protocolSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
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

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_NoAsyncLibrary_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        // AsyncLibraryName is null — not in xcframework mode

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_GenericMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
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

    [Fact]
    public void GetMethodSymbolName_FreeFunction_UsesFreeSegment()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Free", "globalHelper", "$s_mangled_free");
        Assert.StartsWith("SBW_TestModule_Free_globalHelper_", symbol);
    }

    [Fact]
    public void ShouldEmitWrapper_TupleReturn_ReturnsTrue()
    {
        // Tuple returns are now routed through IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var tupleReturn = new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int") });
        var method = CreateMethodWithReturn("getPair", tupleReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DynamicSelfReturn_ClassParent_ReturnsTrue()
    {
        // DynamicSelf (Self) on class parents resolves to class type — returned as class pointer
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var selfReturn = new NamedTypeSpec("Self");
        var method = CreateMethodWithReturn("configure", selfReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DynamicSelfReturn_StructParent_ReturnsFalse()
    {
        // DynamicSelf (Self) on struct parents blocked — Unmanaged requires class type
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var selfReturn = new NamedTypeSpec("Self");
        var method = CreateMethodWithReturn("create", selfReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Emission Tests

    [Fact]
    public void EmitSwiftMethodWrapper_TupleReturn_UsesResultPtr()
    {
        // Tuple returns use resultPtr.initializeMemory(as: (T1, T2).self)
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getPair",
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int") }),
            isClass: true);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Must have resultPtr parameter
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        // Must use initializeMemory for tuple
        Assert.Contains("initializeMemory(as: (Int, Int).self", output);
        // Function signature must not have a return clause (indirect result — Void return)
        Assert.Matches(@"public func _sbw_.*\) \{", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_DynamicSelfReturn_ReturnsClassPointer()
    {
        // DynamicSelf returns use Unmanaged.passRetained().toOpaque()
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "configure",
            new NamedTypeSpec("Self"),
            isClass: true);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Must have UnsafeMutableRawPointer return type
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        // Must use Unmanaged.passRetained for class pointer return
        Assert.Contains("Unmanaged.passRetained(", output);
        Assert.Contains(".toOpaque()", output);
        // Must NOT have resultPtr parameter
        Assert.DoesNotContain("resultPtr", output);
    }

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

    [Fact]
    public void EmitSwiftMethodWrapper_FreeFunction_NoSelfParam()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        method.MangledName = "SBW_TestModule_Free_globalHelper_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_Free_globalHelper_abc12345\")", output);
        Assert.DoesNotContain("self_", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_FreeFunction_CallExpression_NoTypePrefix()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunctionWithReturn("computeValue", new NamedTypeSpec("Swift.Int"), moduleDecl);
        method.MangledName = "SBW_TestModule_Free_computeValue_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Free function call should NOT have a type prefix like "TestModule.MyType.computeValue"
        Assert.DoesNotContain("TestModule.", output.Split("@_cdecl")[1]); // After @_cdecl line, no module prefix in call
        Assert.Contains("computeValue()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_GenericClassParent_EmitsProtocolErasure()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("reset", parentDecl, moduleDecl);
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;
        method.MangledName = "SBW_TestModule_GenericBox_reset";
        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Protocol-based type erasure
        Assert.Contains("private protocol _SBW_P_", output);
        Assert.Contains("func reset()", output);
        Assert.Contains("extension TestModule.GenericBox:", output);
        // Metadata parameter
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        // Self reconstruction via AnyObject
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_P_", output);
        // Should NOT use concrete type
        Assert.DoesNotContain("Unmanaged<TestModule.GenericBox>", output);
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

    private static MethodDecl CreateFreeFunction(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Static,
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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateFreeFunctionWithReturn(string name, TypeSpec returnType, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Static,
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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region Optional<reference-type> Guard Tests

    [Fact]
    public void ShouldEmitWrapper_OptionalClassParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "obj", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "image", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalUnresolvedAppleObjCParam_ReturnsTrue()
    {
        // UIKit.UITableView: no TypeRecord, classified via fallback heuristic
        // (IsOptionalFallbackModule + HasObjCClassPrefix)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UITableView"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "table", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalValueTypeParam_ReturnsTrue()
    {
        // Optional<value-type> params are now handled via @_cdecl UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalStringParam_ReturnsTrue()
    {
        // Optional<String> params are now handled via @_cdecl UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "name", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayParam_StillReturnsFalse()
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
    public void ShouldEmitWrapper_MixedOptionalRefAndValue_ReturnsTrue()
    {
        // Method with both Optional<Class> and Optional<Int> → now allowed
        // Optional<Class> uses nullable pointer ABI, Optional<Int> uses IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var optionalClassSpec = new NamedTypeSpec("Swift.Optional");
        optionalClassSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var optionalIntSpec = new NamedTypeSpec("Swift.Optional");
        optionalIntSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule_doWork",
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
                    SwiftTypeSpec = optionalClassSpec,
                    Name = "obj",
                    PrivateName = "obj",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = optionalIntSpec,
                    Name = "count",
                    PrivateName = "count",
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
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalDoubleReturn_ReturnsTrue()
    {
        // Optional<Double> return now handled via @_cdecl IndirectResult (resultPtr)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        var method = CreateMethodWithReturn("getRate", optionalSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalBoolReturn_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));
        var method = CreateMethodWithReturn("isEnabled", optionalSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DictionaryParam_StillReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", dictSpec, "dict", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClosureReturn_ReturnsTrue()
    {
        // Closure returns now handled via @_cdecl IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureReturn = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureReturn.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateMethodWithReturn("getHandler", closureReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalExistentialReturn_ReturnsFalse()
    {
        // Optional<protocol existential> needs proxy conversion that @_cdecl can't handle
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(protocolList);
        var method = CreateMethodWithReturn("getError", optionalExistential, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region IsOptionalWithReferenceInner Helper Tests

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedAppleFrameworkType_ReturnsTrue()
    {
        // UIKit.UITableView: no TypeRecord but matching fallback heuristic
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UITableView"));

        Assert.True(MethodWrapperEmitter.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedAppleValueType_ReturnsFalse()
    {
        // UIKit.UIEdgeInsets: known Apple value type in ValueTypes exclusion set,
        // excluded by IsKnownValueType in both IsObjCModuleType and IsOptionalObjCBridged
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIEdgeInsets"));

        Assert.False(MethodWrapperEmitter.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedApplePointerType_ReturnsFalse()
    {
        // Swift.UnsafeMutablePointer: rejected by IsPointerType guard
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UnsafeMutablePointer"));

        Assert.False(MethodWrapperEmitter.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NonOptionalType_ReturnsFalse()
    {
        var typeDb = new TypeDatabase();
        Assert.False(MethodWrapperEmitter.IsOptionalWithReferenceInner(new NamedTypeSpec("Swift.Int"), typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NSStringTypedefStruct_ReturnsFalse()
    {
        // NSString typedef structs (e.g., CALayerContentsGravity) are ObjC-bridged in the type
        // database but are Swift structs wrapping NSString — not class instances.
        // Unmanaged<T>.passRetained() requires a class, so these must NOT be classified as
        // reference types. Regression test for NSString typedef carveout.
        var typeDb = new TypeDatabase();
        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/libQuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(quartzModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("QuartzCore.CALayerContentsGravity"));

        Assert.False(MethodWrapperEmitter.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    #endregion
}
