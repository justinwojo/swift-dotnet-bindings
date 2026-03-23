// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for PInvokeSignatureBuilder — verifying return type handling,
/// parameter marshalling, self parameter, library selection, and async/error injection.
/// Uses SignatureHandler.GetPInvokeSignature() as the black-box entry point.
/// </summary>
public class PInvokeEmitterTests
{
    #region Return Type Handling

    [Theory]
    [InlineData("Swift.Int", "long")]
    [InlineData("Swift.Bool", "bool")]
    [InlineData("Swift.Double", "double")]
    [InlineData("Swift.Float", "float")]
    [InlineData("Swift.UInt8", "byte")]
    public void ReturnType_Primitive_MapsDirectly(string swiftType, string expectedReturn)
    {
        var (method, typeDb) = SetupClassMethod("getVal", swiftType);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal(expectedReturn, sig.ReturnType);
    }

    [Fact]
    public void ReturnType_SwiftString_ReturnsSwiftStringBuffer()
    {
        var (method, typeDb) = SetupClassMethod("getName", "Swift.String");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("Swift.SwiftString.Buffer", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_Closure_ReturnsSwiftClosureData()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getCallback", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("SwiftClosureData", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_BoundGenericArray_ReturnsIntPtr()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getItems", classDecl, moduleDecl);
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        method.CSSignature[0] = CreateArg("", arrayType, moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_BoundGenericOptional_ReturnsIntPtr()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getOpt", classDecl, moduleDecl);
        var optType = new NamedTypeSpec("Swift.Optional");
        optType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        method.CSSignature[0] = CreateArg("", optType, moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_ObjCBridged_ReturnsIntPtr()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getObj", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("ObjectiveC.NSObject"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_SwiftClass_ReturnsIntPtr()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Child", "TestModule", "Child",
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getChild", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Child"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_NonFrozenStruct_IndirectResult()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Data", "TestModule", "Data",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getData", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Data"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("void", sig.ReturnType);
        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.Simple("SwiftIndirectResult"));
    }

    [Fact]
    public void ReturnType_FrozenStructWithMemMgmt_ReturnsBuffer()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Props", "TestModule", "Props",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getProps", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Props"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("TestModule.Props.Buffer", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_FrozenStructDirect_ReturnsType()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getPoint", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Point"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("TestModule.Point", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_SimpleEnum_ReturnsUnderlyingType()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterSimpleEnum(testModule, "TestModule.Status", "TestModule", "Status", "Swift.Int");

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getStatus", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Status"), moduleDecl);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("int", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_FrozenComplexEnum_ReturnsIntPtr()
    {
        // Frozen complex enums (non-simple, e.g. with associated values) are C# classes with SafeHandle
        // payloads — non-blittable for LibraryImport, must return IntPtr (SYSLIB1051 fix).
        // Frozen enums bypass the indirect result path, so the return type check must handle them.
        var typeDb = CreateTypeDatabaseWithEnum(TypeRecordFlags.Frozen);
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getVariant", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Variant"), moduleDecl);

        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
    }

    [Fact]
    public void ReturnType_NonFrozenComplexEnum_UsesIndirectResult()
    {
        // Non-frozen complex enums go through the indirect result path (void return + SwiftIndirectResult)
        var typeDb = CreateTypeDatabaseWithEnum(TypeRecordFlags.None);
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getVariant", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Variant"), moduleDecl);

        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("void", sig.ReturnType);
        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.Simple("SwiftIndirectResult"));
    }

    [Fact]
    public void ReturnType_CdeclArrayReturn_VoidWithResultPtr()
    {
        // Bug 2: @_cdecl method returning Swift.Array<T> must use void return + resultPtr.
        // Without the fix, the bound generic path returned IntPtr, mismatching the Swift
        // wrapper's void+resultPtr signature.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getItems", classDecl, moduleDecl);
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        method.CSSignature[0] = CreateArg("", arrayType, moduleDecl);
        method.UsesCdeclMethodWrapper = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("void", sig.ReturnType);
        Assert.Contains(sig.Parameters, p => p.Name == "resultPtr");
    }

    [Fact]
    public void ReturnType_NonCdeclArrayReturn_ReturnsIntPtr()
    {
        // Bug 2 inverse: non-@_cdecl Array returns use IntPtr (bound generic path).
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getItems", classDecl, moduleDecl);
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        method.CSSignature[0] = CreateArg("", arrayType, moduleDecl);
        // No UsesCdeclMethodWrapper

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Equal("IntPtr", sig.ReturnType);
        Assert.DoesNotContain(sig.Parameters, p => p.Name == "resultPtr");
    }

    #endregion

    #region Parameter Marshalling

    [Fact]
    public void Parameter_BoundGenericArray_UsesIntPtr()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        method.CSSignature.Add(CreateArg("items", arrayType, moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var itemsParam = sig.Parameters.First(p => p.Name == "itemsBuffer");
        Assert.True(itemsParam.Type is MarshalledType.Simple("IntPtr"));
    }

    [Fact]
    public void Parameter_ClosureLegacy_UsesSwiftClosureData()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("execute", classDecl, moduleDecl);
        var closure = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        method.CSSignature.Add(CreateArg("callback", closure, moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var callbackParam = sig.Parameters.First(p => p.Name == "callback");
        Assert.True(callbackParam.Type is MarshalledType.SwiftClosureLegacyType);
    }

    [Fact]
    public void Parameter_ClosureCdeclWrapper_UsesFuncPtrAndContext()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("execute", classDecl, moduleDecl);
        method.HasClosureCdeclWrapper = true;
        var closure = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        method.CSSignature.Add(CreateArg("callback", closure, moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Contains(sig.Parameters, p => p.Name == "callbackFuncPtr" && p.Type is MarshalledType.CdeclClosureFuncPtr);
        Assert.Contains(sig.Parameters, p => p.Name == "callbackContext" && p.Type is MarshalledType.CdeclClosureContext);
    }

    [Fact]
    public void Parameter_NonFrozenStruct_UsesSafeHandle()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Data", "TestModule", "Data",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("data", new NamedTypeSpec("TestModule.Data"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var dataParam = sig.Parameters.First(p => p.Name == "data");
        Assert.True(dataParam.Type is MarshalledType.NonFrozenSafeHandleType);
    }

    [Fact]
    public void Parameter_NonFrozenStructAsync_UsesIntPtrFromNonFrozen()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Data", "TestModule", "Data",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl);
        method.IsAsync = true;
        method.CSSignature.Add(CreateArg("data", new NamedTypeSpec("TestModule.Data"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        typeDb.AsyncLibraryName = "SwiftBindings";
        var sig = GetPInvokeSignature(method, typeDb);

        var dataParam = sig.Parameters.First(p => p.Name == "data");
        Assert.True(dataParam.Type is MarshalledType.NonFrozenIntPtrType);
    }

    [Fact]
    public void Parameter_FrozenStructWithMemMgmt_UsesBuffer()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Props", "TestModule", "Props",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("props", new NamedTypeSpec("TestModule.Props"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var propsParam = sig.Parameters.First(p => p.Name == "props");
        Assert.Equal(new MarshalledType.FrozenBuffer("TestModule.Props"), propsParam.Type);
    }

    [Fact]
    public void Parameter_FrozenStructDirect_UsesTypeName()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("point", new NamedTypeSpec("TestModule.Point"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var pointParam = sig.Parameters.First(p => p.Name == "point");
        Assert.Equal(new MarshalledType.Simple("TestModule.Point"), pointParam.Type);
    }

    [Fact]
    public void Parameter_InOut_AddsRefModifier()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("modify", classDecl, moduleDecl);
        var arg = CreateArg("val", new NamedTypeSpec("TestModule.Point"), moduleDecl);
        arg.IsInOut = true;
        method.CSSignature.Add(arg);

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var valParam = sig.Parameters.First(p => p.Name == "val");
        Assert.Equal("ref", valParam.modifier);
    }

    [Fact]
    public void Parameter_ObjCBridged_UsesObjCBridgedMarker()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("obj", new NamedTypeSpec("ObjectiveC.NSObject"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var objParam = sig.Parameters.First(p => p.Name == "obj");
        Assert.True(objParam.Type is MarshalledType.ObjCBridged);
        Assert.Contains("IntPtr", objParam.SignatureString());
    }

    [Fact]
    public void Parameter_EnumNonSimple_UsesEnumSafeHandle()
    {
        var typeDb = CreateTypeDatabaseWithEnum(TypeRecordFlags.None);
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var sig = GetPInvokeSignature(method, typeDb);

        var variantParam = sig.Parameters.First(p => p.Name == "variant");
        Assert.True(variantParam.Type is MarshalledType.EnumSafeHandleType);
        Assert.Contains("IntPtr", variantParam.SignatureString());
    }

    [Fact]
    public void Parameter_SimpleEnum_UsesUnderlyingType()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterSimpleEnum(testModule, "TestModule.Status", "TestModule", "Status", "Swift.Int");

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("status", new NamedTypeSpec("TestModule.Status"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var statusParam = sig.Parameters.First(p => p.Name == "status");
        Assert.True(statusParam.Type is MarshalledType.SimpleEnum);
        Assert.Contains("int", statusParam.PInvokeSignatureString());
    }

    [Fact]
    public void Parameter_Tuple_UsesTupleType()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        method.CSSignature.Add(CreateArg("pair", tupleType, moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var pairParam = sig.Parameters.First(p => p.Name == "pair");
        Assert.True(pairParam.Type is MarshalledType.Simple(var tupleTypeName) && tupleTypeName.Contains("ValueTuple"));
    }

    #endregion

    #region Self Parameter

    [Fact]
    public void Self_StaticMethod_NoSelfParam()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Name == "self" || p.Name == "_self" || p.Name == "_selfClass");
    }

    [Fact]
    public void Self_ClassInstanceMethod_SwiftSelf()
    {
        var (method, typeDb) = SetupClassMethod("doWork", "Swift.Void");
        var sig = GetPInvokeSignature(method, typeDb);

        var selfParam = sig.Parameters.FirstOrDefault(p => p.Name == "self");
        Assert.NotNull(selfParam);
        Assert.True(selfParam.Type is MarshalledType.SwiftSelfUntypedType);
    }

    [Fact]
    public void Self_FrozenStructGetter_SwiftSelfGeneric()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var method = CreateMethod("x_Get", structDecl, moduleDecl, isAccessor: true);

        var typeDb = CreateBasicTypeDatabase(testModule: testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var selfParam = sig.Parameters.FirstOrDefault(p => p.Name == "self");
        Assert.NotNull(selfParam);
        Assert.True(selfParam.Type is MarshalledType.SwiftSelfTyped("Point"));
    }

    [Fact]
    public void Self_FrozenStructWithMemMgmt_Getter_SwiftSelfBuffer()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Props", "TestModule", "Props",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Props", moduleDecl);
        var method = CreateMethod("name_Get", structDecl, moduleDecl, isAccessor: true);

        var typeDb = CreateBasicTypeDatabase(testModule: testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var selfParam = sig.Parameters.FirstOrDefault(p => p.Name == "self");
        Assert.NotNull(selfParam);
        Assert.True(selfParam.Type is MarshalledType.SwiftSelfTyped("Props.Buffer"));
    }

    [Fact]
    public void Self_FrozenStructSetter_SwiftSelfPointer()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var method = CreateMethod("x_Set", structDecl, moduleDecl, isAccessor: true);
        method.CSSignature.Add(CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase(testModule: testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var selfParam = sig.Parameters.FirstOrDefault(p => p.Name == "self");
        Assert.NotNull(selfParam);
        Assert.True(selfParam.Type is MarshalledType.SwiftSelfUntypedType);
    }

    [Fact]
    public void Self_FreeFunctionWrapperForClass_IntPtrSelfClass()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.UsesFreeFunctionWrapper = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var selfParam = sig.Parameters.FirstOrDefault(p => p.Name == "_selfClass");
        Assert.NotNull(selfParam);
        Assert.Equal(new MarshalledType.Simple("IntPtr"), selfParam.Type);
    }

    #endregion

    #region Library Selection + Entry Point

    [Fact]
    public void EmitPInvoke_NonFinalClassInstanceMethod_AppendsTjSuffix()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;

        var emitted = EmitPInvokeToString(method, CreateBasicTypeDatabase("Loader"));

        Assert.Contains("Tj\"", emitted); // EntryPoint ends with Tj
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\"", emitted); // module library path
    }

    [Fact]
    public void EmitPInvoke_FinalClass_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = true;
        var method = CreateMethod("doWork", classDecl, moduleDecl);

        var emitted = EmitPInvokeToString(method, CreateBasicTypeDatabase("Loader"));

        Assert.DoesNotContain("Tj\"", emitted);
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\"", emitted);
    }

    [Fact]
    public void EmitPInvoke_AsyncMethod_UsesAsyncLibrary()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var emitted = EmitPInvokeToString(method, typeDb);

        Assert.Contains("[LibraryImport(\"SwiftBindings\"", emitted); // async library, not module
    }

    [Fact]
    public void EmitPInvoke_WrapperLibMethod_UsesAsyncLibrary()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);
        method.UsesWrapperLibrary = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var emitted = EmitPInvokeToString(method, typeDb);

        Assert.Contains("[LibraryImport(\"SwiftBindings\"", emitted);
    }

    [Fact]
    public void EmitPInvoke_WrapperLibMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;
        method.UsesWrapperLibrary = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var emitted = EmitPInvokeToString(method, typeDb);

        Assert.DoesNotContain("Tj\"", emitted); // wrapper methods skip Tj
    }

    [Fact]
    public void EmitPInvoke_FinalMemberInNonFinalClass_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = true;

        var emitted = EmitPInvokeToString(method, CreateBasicTypeDatabase("Loader"));

        Assert.DoesNotContain("Tj\"", emitted); // final member uses direct dispatch
    }

    [Fact]
    public void EmitPInvoke_ExtensionMethodOnNonFinalClass_NoTjSuffix()
    {
        // Extension methods use static dispatch — no vtable entry, no Tj thunk symbol.
        // This is critical for cross-module extensions (e.g., StripePayments extending
        // StripeCore.STPAPIClient) where Tj thunks don't exist in any binary.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;
        method.IsExtensionMethod = true;

        var emitted = EmitPInvokeToString(method, CreateBasicTypeDatabase("Loader"));

        Assert.DoesNotContain("Tj\"", emitted); // extension methods use direct dispatch
    }

    [Fact]
    public void ComputeEntryPoint_RegularInstanceMethod_AppendsTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.EndsWith("Tj", entryPoint);
    }

    [Fact]
    public void ComputeEntryPoint_ExtensionMethod_NoTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;
        method.IsExtensionMethod = true;

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.DoesNotContain("Tj", entryPoint);
    }

    [Fact]
    public void ComputeEntryPoint_FinalClass_NoTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = true;
        var method = CreateMethod("doWork", classDecl, moduleDecl);

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.DoesNotContain("Tj", entryPoint);
    }

    [Fact]
    public void ComputeEntryPoint_StaticMethod_NoTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.DoesNotContain("Tj", entryPoint);
    }

    [Fact]
    public void ComputeEntryPoint_Constructor_NoTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("init", classDecl, moduleDecl, isConstructor: true);

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.DoesNotContain("Tj", entryPoint);
    }

    [Fact]
    public void ComputeEntryPoint_ExtensionPropertyAccessor_NoTj()
    {
        // Property accessor MethodDecls should inherit IsExtensionMethod from
        // the parent property node's isFromExtension flag.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("name_Get", classDecl, moduleDecl, isAccessor: true);
        method.IsFinal = false;
        method.IsExtensionMethod = true;

        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.DoesNotContain("Tj", entryPoint);
    }

    #endregion

    #region Async/Error Parameters

    [Fact]
    public void AsyncMethod_AddsCallbackParams()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.AsyncCallbackType);
        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.AsyncErrorCallbackType);
        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.AsyncTaskType);
    }

    [Fact]
    public void ThrowingMethod_AddsSwiftErrorParam()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("parse", classDecl, moduleDecl);
        method.Throws = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var errorParam = sig.Parameters.FirstOrDefault(p => p.Name == "swiftError");
        Assert.NotNull(errorParam);
        Assert.Equal(new MarshalledType.Simple("SwiftError"), errorParam.Type);
        Assert.Equal("ref", errorParam.modifier);
    }

    [Fact]
    public void AsyncAndThrowing_AsyncSkipsSwiftError()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("riskyFetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;
        method.Throws = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Name == "error");
        Assert.Contains(sig.Parameters, p => p.Type is MarshalledType.AsyncCallbackType);
    }

    [Fact]
    public void NonAsyncNonThrowing_NoExtraParams()
    {
        var (method, typeDb) = SetupClassMethod("doWork", "Swift.Void");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Type is MarshalledType.AsyncCallbackType);
        Assert.DoesNotContain(sig.Parameters, p => p.Type is MarshalledType.AsyncErrorCallbackType);
        Assert.DoesNotContain(sig.Parameters, p => p.Type is MarshalledType.AsyncTaskType);
        Assert.DoesNotContain(sig.Parameters, p => p.Type is MarshalledType.Simple("SwiftError"));
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NoSelfParam()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("init", classDecl, moduleDecl, isConstructor: true);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Name == "self");
    }

    [Fact]
    public void Constructor_StaticMethod_NoAsyncParams()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Type is MarshalledType.AsyncCallbackType);
        Assert.DoesNotContain(sig.Parameters, p => p.Name == "self");
    }

    [Fact]
    public void ThrowingCdeclConstructor_ErrorParamBeforeArgs()
    {
        // Issue L: For @_cdecl constructor wrappers, Swift places errorOut BEFORE regular
        // parameters: (resultPtr, errorOut, args...). C# P/Invoke must match this ordering.
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.DataCache", "TestModule", "DataCache",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("DataCache", moduleDecl);
        var method = CreateMethod("init", structDecl, moduleDecl, isConstructor: true);
        method.Throws = true;
        method.UsesCdeclConstructorWrapper = true;
        // Add a String parameter (name)
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("DataCache", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        // Find errorPtr and name parameter indices
        var errorIdx = -1;
        var nameIdx = -1;
        for (int i = 0; i < sig.Parameters.Count; i++)
        {
            if (sig.Parameters[i].Name == "errorPtr") errorIdx = i;
            if (sig.Parameters[i].Name == "name") nameIdx = i;
        }

        Assert.True(errorIdx >= 0, "errorPtr parameter should exist");
        Assert.True(nameIdx >= 0, "name parameter should exist");
        Assert.True(errorIdx < nameIdx, $"errorPtr (index {errorIdx}) should come before name (index {nameIdx})");
    }

    [Fact]
    public void NonThrowingCdeclConstructor_NoErrorParam()
    {
        // Non-throwing @_cdecl constructors should not have an error parameter
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Config", "TestModule", "Config",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Config", moduleDecl);
        var method = CreateMethod("init", structDecl, moduleDecl, isConstructor: true);
        method.Throws = false;
        method.UsesCdeclConstructorWrapper = true;
        method.CSSignature.Add(CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Config", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.DoesNotContain(sig.Parameters, p => p.Name == "errorPtr");
    }

    [Fact]
    public void ThrowingNonCdeclMethod_ErrorParamLast()
    {
        // Normal throwing methods (non-cdecl) should have error param AFTER args
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("parse", classDecl, moduleDecl);
        method.Throws = true;
        method.CSSignature.Add(CreateArg("data", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader");
        var sig = GetPInvokeSignature(method, typeDb);

        var errorIdx = -1;
        var dataIdx = -1;
        for (int i = 0; i < sig.Parameters.Count; i++)
        {
            if (sig.Parameters[i].Name == "swiftError") errorIdx = i;
            if (sig.Parameters[i].Name == "data") dataIdx = i;
        }

        Assert.True(errorIdx >= 0, "swiftError parameter should exist");
        Assert.True(dataIdx >= 0, "data parameter should exist");
        Assert.True(errorIdx > dataIdx, $"swiftError (index {errorIdx}) should come after data (index {dataIdx})");
    }

    [Fact]
    public void CdeclFrozenStructConstructor_HasResultPtrParam()
    {
        // @_cdecl frozen struct constructors must pass resultPtr as the first parameter
        // because the Swift wrapper takes UnsafeMutableRawPointer and returns void.
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Transformer", "TestModule", "Transformer",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Transformer", moduleDecl);
        var method = CreateMethod("init", structDecl, moduleDecl, isConstructor: true);
        method.UsesCdeclConstructorWrapper = true;
        method.CSSignature.Add(CreateArg("offset", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Transformer", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        // Should have resultPtr as a parameter
        Assert.Contains(sig.Parameters, p => p.Name == "resultPtr");
        // Return type should be void (result written to buffer, not returned)
        Assert.Equal("void", sig.ReturnType);
    }

    [Fact]
    public void CdeclFrozenStructConstructor_ResultPtrBeforeArgs()
    {
        // resultPtr should come before regular constructor arguments
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Config", "TestModule", "Config",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Config", moduleDecl);
        var method = CreateMethod("init", structDecl, moduleDecl, isConstructor: true);
        method.UsesCdeclConstructorWrapper = true;
        method.CSSignature.Add(CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Config", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var resultIdx = -1;
        var valueIdx = -1;
        for (int i = 0; i < sig.Parameters.Count; i++)
        {
            if (sig.Parameters[i].Name == "resultPtr") resultIdx = i;
            if (sig.Parameters[i].Name == "value") valueIdx = i;
        }

        Assert.True(resultIdx >= 0, "resultPtr parameter should exist");
        Assert.True(valueIdx >= 0, "value parameter should exist");
        Assert.True(resultIdx < valueIdx,
            $"resultPtr (index {resultIdx}) should come before value (index {valueIdx})");
    }

    [Fact]
    public void NonCdeclFrozenStructConstructor_NoResultPtrParam()
    {
        // Non-@_cdecl frozen struct constructors should NOT have an explicit resultPtr
        // parameter — they use SwiftIndirectResult (via CallConvSwift).
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Point", "TestModule", "Point",
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var method = CreateMethod("init", structDecl, moduleDecl, isConstructor: true);
        // UsesCdeclConstructorWrapper is NOT set

        var typeDb = CreateBasicTypeDatabase("Point", testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        // Should NOT have resultPtr (Cdecl-style) — uses SwiftIndirectResult instead
        Assert.DoesNotContain(sig.Parameters, p => p.Name == "resultPtr");
    }

    #endregion

    #region Signature String Tests

    [Fact]
    public void PInvokeParametersString_AsyncCallback_EmitsVoidStar()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;

        var typeDb = CreateBasicTypeDatabase("Loader");
        typeDb.AsyncLibraryName = "SwiftBindings";
        var sig = GetPInvokeSignature(method, typeDb);

        var paramsStr = sig.PInvokeParametersString();
        Assert.Contains("void*", paramsStr);
        Assert.Contains("IntPtr", paramsStr);
    }

    [Fact]
    public void CallArgumentsString_EnumSafeHandle_ExtractsPayload()
    {
        var typeDb = CreateTypeDatabaseWithEnum(TypeRecordFlags.None);
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Contains("variant.Payload.DangerousGetHandle()", sig.CallArgumentsString());
    }

    [Fact]
    public void CallArgumentsString_IntPtrFromNonFrozen_AppendsHandle()
    {
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        RegisterType(testModule, "TestModule.Data", "TestModule", "Data",
            TypeRecordFlags.None, TypeRecordKind.Struct);

        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;
        method.CSSignature.Add(CreateArg("data", new NamedTypeSpec("TestModule.Data"), moduleDecl));

        var typeDb = CreateBasicTypeDatabase("Loader", testModule);
        typeDb.AsyncLibraryName = "SwiftBindings";
        var sig = GetPInvokeSignature(method, typeDb);

        Assert.Contains("dataHandle", sig.CallArgumentsString());
    }

    #endregion

    #region NativeThunk P/Invoke Signatures

    [Fact]
    public void NativeThunk_InstanceMethod_UsesIntPtrSelf_NotSwiftSelf()
    {
        // NativeThunk instance methods should use plain IntPtr for self (like @_cdecl),
        // not SwiftSelf which uses the x20 register under CallConvSwift.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Widget", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.WrapperStrategy = WrapperStrategy.NativeThunk;

        var typeDb = CreateBasicTypeDatabase("Widget");
        var sig = GetPInvokeSignature(method, typeDb);

        // Should contain IntPtr self parameter, not SwiftSelf
        var paramStr = sig.PInvokeParametersString();
        Assert.Contains("IntPtr _selfClass", paramStr);
        Assert.DoesNotContain("SwiftSelf", paramStr);
    }

    [Fact]
    public void NativeThunk_ThrowingMethod_UsesOutIntPtrError_NotSwiftError()
    {
        // NativeThunk throwing methods should use 'out IntPtr errorPtr' (like @_cdecl),
        // not 'ref SwiftError swiftError' which uses the x21 register under CallConvSwift.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Parser", moduleDecl);
        var method = CreateMethod("parse", classDecl, moduleDecl);
        method.WrapperStrategy = WrapperStrategy.NativeThunk;
        method.Throws = true;

        var typeDb = CreateBasicTypeDatabase("Parser");
        var sig = GetPInvokeSignature(method, typeDb);

        var paramStr = sig.PInvokeParametersString();
        Assert.Contains("out IntPtr errorPtr", paramStr);
        Assert.DoesNotContain("SwiftError", paramStr);
    }

    [Fact]
    public void NativeThunk_IndirectResult_UsesSwiftIndirectResult_NotIntPtrResultPtr()
    {
        // NativeThunk methods with indirect result should use 'SwiftIndirectResult' (x8 register),
        // NOT 'IntPtr resultPtr'. Thunks rely on AAPCS64's hidden x8 register for struct return
        // buffers — the thunk prologue does 'mov x19, x8' to save the buffer. If C# puts resultPtr
        // in x0 (as a regular IntPtr param), the thunk reads x8 which is something else entirely.
        // Only @_cdecl wrappers use IntPtr resultPtr (because the Swift wrapper has resultPtr as
        // an explicit parameter in x0).
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getData", classDecl, moduleDecl);
        method.WrapperStrategy = WrapperStrategy.NativeThunk;
        // Return a non-frozen struct type which triggers indirect result
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Data"), moduleDecl);

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
            SwiftTypeName.FromModuleQualifiedName("TestModule.Data"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Data"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Data"),
                MetadataAccessor = "$s10TestModule4DataVMa",
                Flags = TypeRecordFlags.None, // Non-frozen → triggers indirect result
                Kind = TypeRecordKind.Struct
            });
        var typeDb = CreateBasicTypeDatabase(testModule: testModule);
        var sig = GetPInvokeSignature(method, typeDb);

        var paramStr = sig.PInvokeParametersString();
        Assert.Contains("SwiftIndirectResult", paramStr);
        Assert.DoesNotContain("IntPtr resultPtr", paramStr);
        Assert.Equal("void", sig.ReturnType);
    }

    [Fact]
    public void NativeThunk_FrozenStructSelf_UsesIntPtrSelf_NotSwiftSelfTyped()
    {
        // NativeThunk instance methods on frozen structs should use IntPtr for self (like @_cdecl),
        // not SwiftSelf<T> which uses the x20 register under CallConvSwift.
        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var method = CreateMethod("magnitude", structDecl, moduleDecl);
        method.WrapperStrategy = WrapperStrategy.NativeThunk;
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.Double"), moduleDecl);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        var typeDb = CreateBasicTypeDatabase(testModule: testModule);

        var sig = GetPInvokeSignature(method, typeDb);

        var paramStr = sig.PInvokeParametersString();
        Assert.Contains("IntPtr _selfFixed", paramStr);
        Assert.DoesNotContain("SwiftSelf", paramStr);
    }

    [Fact]
    public void NativeThunk_CallingConvention_IsCdecl()
    {
        // NativeThunk methods should use CallConvCdecl, not CallConvSwift.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Widget", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.WrapperStrategy = WrapperStrategy.NativeThunk;

        var convention = WrapperValidation.GetCallingConvention(method);
        Assert.Equal(PInvokeCallingConvention.Cdecl, convention);
    }

    #endregion

    #region Helper Methods

    private static Signature GetPInvokeSignature(MethodDecl method, TypeDatabase typeDb)
    {
        var env = new MethodEnvironment(method, typeDb);
        var handler = new SignatureHandler(env);
        return handler.GetPInvokeSignature();
    }

    private static string EmitPInvokeToString(MethodDecl method, TypeDatabase typeDb)
    {
        var env = new MethodEnvironment(method, typeDb);
        var handler = new SignatureHandler(env);
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        PInvokeEmitter.EmitPInvoke(csWriter, env, handler);
        csWriter.Flush();
        return sw.ToString();
    }

    private static (MethodDecl method, TypeDatabase typeDb) SetupClassMethod(string name, string returnSwiftType)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod(name, classDecl, moduleDecl);
        if (returnSwiftType != "Swift.Void")
            method.CSSignature[0] = CreateArg("", new NamedTypeSpec(returnSwiftType), moduleDecl);
        var typeDb = CreateBasicTypeDatabase("Loader");
        return (method, typeDb);
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

    private static MethodDecl CreateMethod(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        bool isStatic = false,
        bool isConstructor = false,
        bool isAccessor = false)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name.Length}{name}SiyF",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = isConstructor,
            IsAccessor = isAccessor,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
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

    private static TypeDatabase CreateBasicTypeDatabase(string className = null, ModuleTypeDatabase testModule = null)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int", "System", "Int64", "$sSiMa");
        RegisterPrimitive(swiftModule, "Swift.Bool", "System", "Boolean", "$sSbMa");
        RegisterPrimitive(swiftModule, "Swift.Double", "System", "Double", "$sSdMa");
        RegisterPrimitive(swiftModule, "Swift.Float", "System", "Single", "$sSfMa");
        RegisterPrimitive(swiftModule, "Swift.UInt8", "System", "Byte", "$ss5UInt8VMa");
        RegisterPrimitive(swiftModule, "Swift.String", "Swift", "SwiftString", "$sSSMa",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        typeDb.AddModuleDatabase(swiftModule);

        if (testModule == null)
            testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        if (className != null)
        {
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", className),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
                    MetadataAccessor = $"$s10TestModule{className.Length}{className}CMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                });
        }
        typeDb.AddModuleDatabase(testModule);

        return typeDb;
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

    private static void RegisterSimpleEnum(ModuleTypeDatabase module, string swiftName, string csNamespace, string csName, string rawValueType)
    {
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType
            });
    }

    private static TypeDatabase CreateTypeDatabaseWithEnum(TypeRecordFlags enumFlags)
    {
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
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = enumFlags,
                Kind = TypeRecordKind.Enum
            });

        return CreateBasicTypeDatabase(testModule: testModule);
    }

    #endregion
}
