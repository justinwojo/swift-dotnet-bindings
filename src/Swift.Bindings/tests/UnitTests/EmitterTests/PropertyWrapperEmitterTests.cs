// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for PropertyWrapperEmitter: per-property @_cdecl wrappers that route
/// property getter/setter P/Invokes through C calling convention to avoid CallConvSwift crashes.
/// </summary>
public class PropertyWrapperEmitterTests
{
    #region ShouldEmitWrapper Guard Tests

    [Fact]
    public void ShouldEmitWrapper_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("name", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
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
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClosureProperty_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var (propertyDecl, env) = CreatePropertyAndEnv("handler", closureType, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncProperty_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateAccessorMethod("getter:name", isGetter: true, parentDecl, moduleDecl);
        getterMethod.IsAsync = true;

        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialProperty_ReturnsTrue()
    {
        // Existential properties are now supported in @_cdecl wrappers via indirect result pointer
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.DataCaching", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") });
        var (propertyDecl, env) = CreatePropertyAndEnv("cache", protocolListSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCopyableStructParent_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MoveOnly");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MoveOnly", moduleDecl);
        // Non-copyable: has Escapable but NOT Copyable
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MoveOnly"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule8MoveOnlyVACSWAAMc")
        };
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonGenericClass_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("name", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonGenericStruct_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    #endregion

    #region Symbol Naming Tests

    [Fact]
    public void GetAccessorSymbolName_Getter_CorrectFormat()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline", "configuration", isGetter: true);
        Assert.Equal("SBW_Get_Nuke_ImagePipeline_configuration", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_Setter_CorrectFormat()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline", "configuration", isGetter: false);
        Assert.Equal("SBW_Set_Nuke_ImagePipeline_configuration", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_NestedType_DotReplacedWithUnderscore()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline.Configuration", "dataLoader", isGetter: true);
        Assert.Equal("SBW_Get_Nuke_ImagePipeline_Configuration_dataLoader", symbol);
    }

    #endregion

    #region Getter Wrapper Swift Emission Tests

    [Fact]
    public void EmitSwiftGetterWrapper_PrimitiveInt_DirectReturn()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Get_TestModule_MyType_count\")", output);
        Assert.Contains("-> Int", output);
        Assert.Contains("let obj = Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
        Assert.Contains("return obj.count", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_Bool_ReturnsInt8()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "isEnabled", new NamedTypeSpec("Swift.Bool"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_isEnabled";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int8", output);
        Assert.Contains("return obj.isEnabled ? 1 : 0", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_String_SBWUtf8Slice()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "name", new NamedTypeSpec("Swift.String"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_name";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // String returns via resultPtr (@_cdecl can't return Swift structs)
        Assert.Contains("resultPtr: UnsafeMutableRawPointer", output);
        Assert.DoesNotContain("-> SBW_Utf8Slice", output);
        Assert.Contains("let result = obj.name", output);
        Assert.Contains("let utf8 = Array(result.utf8)", output);
        Assert.Contains("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count)", output);
        // Also emits the SBW_Utf8Slice struct
        Assert.Contains("@frozen", output);
        Assert.Contains("public struct SBW_Utf8Slice", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_SimpleEnum_ReturnsRawValue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ContentMode", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ContentMode");
        var getterMethod = CreateAccessorMethod("getter:mode", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "mode",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_mode";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int", output);
        Assert.Contains("rawValue", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_Class_ReturnsUnmanagedPointer()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ChildObj");
        var getterMethod = CreateAccessorMethod("getter:child", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "child",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_child";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged.passRetained(obj.child).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_NonFrozenStruct_UsesResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.Config");
        var getterMethod = CreateAccessorMethod("getter:config", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "config",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_config";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("initializeMemory", output);
        Assert.DoesNotContain("->", output); // void return
    }

    [Fact]
    public void EmitSwiftGetterWrapper_StaticProperty_NoSelfParam()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "shared", new NamedTypeSpec("Swift.Int"), isClass: true, isStatic: true);

        var symbol = "SBW_Get_TestModule_MyType_shared";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.shared", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_StructProperty_UsesUnsafeRawPointerSelf()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "value", new NamedTypeSpec("Swift.Int"), isClass: false);

        var symbol = "SBW_Get_TestModule_MyType_value";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_MainActorIsolated_HasAnnotation()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true, isMainActorIsolated: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
    }

    #endregion

    #region Setter Wrapper Swift Emission Tests

    [Fact]
    public void EmitSwiftSetterWrapper_PrimitiveInt_DirectAssign()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Set_TestModule_MyType_count\")", output);
        Assert.Contains("_ newValue: Int", output);
        Assert.Contains("obj.count = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_Bool_Int8Param()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "isEnabled", new NamedTypeSpec("Swift.Bool"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_isEnabled";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: Int8", output);
        Assert.Contains("newValueVal = newValue != 0", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_String_Utf8PointerAndLength()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "name", new NamedTypeSpec("Swift.String"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_name";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ utf8Ptr: UnsafePointer<UInt8>", output);
        Assert.Contains("_ utf8Len: Int", output);
        Assert.Contains("String(bytes: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), encoding: .utf8)!", output);
        Assert.Contains("obj.name = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_ClassProperty_SetOnReconstructedObj()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ChildObj");
        var setterMethod = CreateAccessorMethod("setter:child", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "child",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Set_TestModule_MyType_child";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged<ChildObj>.fromOpaque(newValue).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_StructParent_MutatesThroughPointer()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "value", new NamedTypeSpec("Swift.Int"), isClass: false);

        var symbol = "SBW_Set_TestModule_MyType_value";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee.value = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_StaticProperty_NoSelfParam()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "shared", new NamedTypeSpec("Swift.Int"), isClass: true, isStatic: true);

        var symbol = "SBW_Set_TestModule_MyType_shared";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.shared = newValue", output);
    }

    #endregion

    #region Dedup Tests

    [Fact]
    public void EmitSwiftGetterWrapper_SameSymbolTwice_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Only one @_cdecl annotation
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_SameSymbolTwice_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    #endregion

    #region GetCdeclReturnMapping Tests

    [Fact]
    public void GetCdeclReturnMapping_Int_DirectReturn()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("Swift.Int"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.Direct, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_Bool_Int8()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("Swift.Bool"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int8", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.Bool, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_String_SBWUtf8Slice()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("Swift.String"), typeDb);

        Assert.True(needsPtr); // String returns via resultPtr (@_cdecl can't return Swift structs)
        Assert.Equal("SBW_Utf8Slice", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.String, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_Class_Pointer()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("TestModule.ChildObj"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("UnsafeMutableRawPointer", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.ClassPointer, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_SimpleEnum_RawValueType()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ContentMode", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("TestModule.ContentMode"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_NonFrozenStruct_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("TestModule.Config"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_ProtocolExistential_NeedsResultPtr()
    {
        // Protocol existentials are not C-representable in @_cdecl — must use indirect result
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(protocolListSpec, typeDb);

        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_ComplexEnum_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ResultType", TypeRecordFlags.None, TypeRecordKind.Enum));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("TestModule.ResultType"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_GenericOptional_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(optionalSpec, typeDb);

        Assert.True(needsPtr);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_FrozenBlittableStruct_NeedsResultPtr()
    {
        // @_cdecl can't return Swift structs (even @frozen ones), so all structs use resultPtr
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Point", TypeRecordFlags.Frozen, TypeRecordKind.Struct));
        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("TestModule.Point"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_NSStringTypedef_IndirectResult_NotClassPointer()
    {
        // NSString typedef structs (e.g., CALayerContentsGravity) are registered as kind="class"
        // with ObjCBridged in the XML database, but they are Swift structs wrapping NSString.
        // Unmanaged.passRetained() is invalid for these — must route through IndirectResult.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/QuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class  // XML says kind="class" but it's really a struct typedef
            });
        typeDb.AddModuleDatabase(quartzModule);

        var (mapping, needsPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(
            new NamedTypeSpec("QuartzCore.CALayerContentsGravity"), typeDb);

        // Must NOT be ClassPointer (would emit Unmanaged.passRetained which crashes on a struct)
        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.cdeclReturnType);
        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void MethodRequiresIndirectResult_NSStringTypedef_CdeclPropertyWrapper_ReturnsTrue()
    {
        // Verifies that MethodRequiresIndirectResult agrees with GetCdeclReturnMapping
        // for NSString typedef types (kind="class" + ObjCBridged in XML database).
        // Without this check, the class check at line 162 would return false (no indirect result),
        // while the Swift side expects resultPtr — ABI mismatch.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/QuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(quartzModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "QuartzCore",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = CreateClassDecl("CALayer", moduleDecl);
        parentDecl.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayer");

        var method = new MethodDecl
        {
            Name = "getter:contentsGravity",
            MangledName = "$s_test_getter",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            UsesCdeclPropertyWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("QuartzCore.CALayerContentsGravity"),
                    Name = "_result",
                    PrivateName = "_result",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
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
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ExistentialReturn_CdeclPropertyWrapper_ReturnsTrue()
    {
        // Protocol existential returns must use indirect result in @_cdecl wrappers
        // because existential containers are not C-representable.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });

        var method = new MethodDecl
        {
            Name = "getter:cache",
            MangledName = "$s_test_getter_cache",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            UsesCdeclPropertyWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = protocolListSpec,
                    Name = "_result",
                    PrivateName = "_result",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
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
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    #endregion

    #region MethodDecl Flag Tests

    [Fact]
    public void UsesCdeclWrapper_PropertyWrapperSet_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "getter:count",
            MangledName = "$test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclPropertyWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
        Assert.False(method.UsesCdeclConstructorWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_ConstructorWrapperSet_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$test",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclConstructorWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
        Assert.False(method.UsesCdeclPropertyWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_NeitherSet_ReturnsFalse()
    {
        var method = new MethodDecl
        {
            Name = "doSomething",
            MangledName = "$test",
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

    private static (SwiftWriter swiftWriter, StringWriter sw, PropertyDecl propertyDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateGetterTestSetup(
        string propertyName, TypeSpec typeSpec, bool isClass, bool isStatic = false, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var getterMethod = CreateAccessorMethod($"getter:{propertyName}", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, propertyDecl, env, ctx);
    }

    private static (SwiftWriter swiftWriter, StringWriter sw, PropertyDecl propertyDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateSetterTestSetup(
        string propertyName, TypeSpec typeSpec, bool isClass, bool isStatic = false, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var setterMethod = CreateAccessorMethod($"setter:{propertyName}", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, propertyDecl, env, ctx);
    }

    private static (PropertyDecl propertyDecl, MethodEnvironment env) CreatePropertyAndEnv(
        string propertyName, TypeSpec typeSpec, TypeDecl parentDecl, ModuleDecl moduleDecl, TypeDatabase typeDb)
    {
        var getterMethod = CreateAccessorMethod($"getter:{propertyName}", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        return (propertyDecl, env);
    }

    private static MethodDecl CreateAccessorMethod(string name, bool isGetter, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_accessor_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
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

    /// <summary>
    /// Overload without rawValueTypeName for convenience.
    /// </summary>
    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        return CreateTestEnvironmentWithExtraTypes(
            typeName,
            extraTypes.Select(t => (t.qualifiedName, t.flags, t.kind, (string?)null)).ToArray());
    }

    #endregion

    #region Optional<reference-type> Guard Tests

    [Fact]
    public void ShouldEmitWrapper_OptionalClassProperty_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));
        var (propertyDecl, env) = CreatePropertyAndEnv("child", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedReadOnlyProperty_ReturnsTrue()
    {
        // Getter-only ObjC optional passes — getter side is calling-convention agnostic
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));
        // getter-only property
        var (propertyDecl, env) = CreatePropertyAndEnv("image", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedReadWriteProperty_ReturnsFalse()
    {
        // setter + ObjC → blocked due to IntPtr alias incompatibility
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));

        var getterMethod = CreateAccessorMethod("getter:image", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:image", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "image",
            SwiftTypeSpec = optionalSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalValueTypeProperty_ReturnsTrue()
    {
        // Optional<value-type> properties now handled via @_cdecl IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var (propertyDecl, env) = CreatePropertyAndEnv("count", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalDoubleProperty_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        var (propertyDecl, env) = CreatePropertyAndEnv("rate", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayProperty_StillReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var (propertyDecl, env) = CreatePropertyAndEnv("items", arraySpec, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalExistentialProperty_ReturnsFalse()
    {
        // Optional<protocol existential> needs proxy conversion that @_cdecl can't handle
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(protocolList);
        var (propertyDecl, env) = CreatePropertyAndEnv("error", optionalExistential, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    #endregion
}
