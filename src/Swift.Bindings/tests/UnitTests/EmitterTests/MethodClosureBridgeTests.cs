// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the MethodClosureBridge emitter — handles regular methods with closure
/// parameters whose closure argument types include bound generics.
/// </summary>
public class MethodClosureBridgeTests
{
    // ─── IsEligible ───────────────────────────────────────────────────

    [Fact]
    public void IsEligible_MethodWithBoundGenericClosureArg_ReturnsTrue()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MethodWithPrimitiveOnlyClosureArgs_ReturnsFalse()
    {
        // Closures with all-primitive args go through the normal ClosureEmitter pipeline
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.IsAsync = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.Throws = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ProtocolExtensionMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.IsProtocolExtensionMethod = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClosureWithObjCBridgedGenericArg_ReturnsFalse()
    {
        // ObjC-bridged types don't implement ISwiftObject, so they can't be
        // generic args in bound generic types with ISwiftObject constraints.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // DataResponse<NSError> — NSError is ObjC-bridged
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("Foundation.NSError"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("callback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── TryEmit: Swift Wrapper ───────────────────────────────────────

    [Fact]
    public void TryEmit_BoolClosureArg_EmitsUInt8ConversionInSwiftWrapper()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Swift cdecl type must use UInt8 for Bool, not Bool
        Assert.Contains("UInt8", swift);
        // Bool args must be converted: (__p1 ? 1 : 0)
        Assert.Contains("? 1 : 0)", swift);
    }

    [Fact]
    public void TryEmit_BoolReturnClosure_EmitsUInt8ToBoolConversion()
    {
        var (method, typeDatabase, env) = CreateMethodWithBoolReturnClosure();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // cdecl returns UInt8, original expects Bool → need != 0
        Assert.Contains("!= 0", swift);
    }

    [Fact]
    public void TryEmit_ValueTypeClosureArg_EmitsWithUnsafePointer()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Bound generic value types need withUnsafePointer wrapping
        Assert.Contains("withUnsafePointer(to:", swift);
        Assert.Contains("UnsafeMutableRawPointer(mutating:", swift);
    }

    // ─── TryEmit: C# Callback ─────────────────────────────────────────

    [Fact]
    public void TryEmit_PrimitiveClosureArg_UsesTypedCallbackParam()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Bool closure args should be "byte" in callback, not IntPtr
        Assert.Contains("byte arg1", cs);
        // Bound generic closure args stay as IntPtr
        Assert.Contains("IntPtr arg0", cs);
    }

    [Fact]
    public void TryEmit_PrimitiveClosureArg_FunctionPointerFieldUsesTypedArgs()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Function pointer delegate type should have typed args, not all IntPtr
        // delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>", cs);
    }

    [Fact]
    public void TryEmit_BoolClosureArgInCallback_EmitsByteConversion()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Inner delegate in public method should use byte for Bool arg
        Assert.Contains("byte __p1", cs);
        // Bool arg marshal: __p1 != 0 (not __p1 != IntPtr.Zero)
        Assert.Contains("__p1 != 0", cs);
    }

    // ─── TryEmit: ObjC-bridged non-closure params ─────────────────────

    [Fact]
    public void TryEmit_ObjCBridgedParam_UsesHandle()
    {
        var (method, typeDatabase, env) = CreateMethodWithObjCParam();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // ObjC-bridged params use .Handle, not .Payload.DangerousGetHandle()
        Assert.Contains("presenter.Handle", cs);
        // The non-closure param should NOT use .Payload (SwiftSelf for 'self' is separate)
        Assert.DoesNotContain("presenter.Payload", cs);
    }

    [Fact]
    public void TryEmit_SwiftClassParam_UsesPayload()
    {
        var (method, typeDatabase, env) = CreateMethodWithSwiftClassParam();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Swift-native class params use .Payload.DangerousGetHandle()
        Assert.Contains(".Payload.DangerousGetHandle()", cs);
    }

    // ─── TryEmit: Static methods ──────────────────────────────────────

    [Fact]
    public void TryEmit_StaticMethod_EmitsSelfDotInSwift()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Static methods use "Self." not "self."
        Assert.Contains("Self.", swift);
        Assert.Contains("public static func", swift);

        var cs = csOutput.ToString();
        // Static methods don't use SwiftSelf
        Assert.DoesNotContain("SwiftSelf", cs);
        Assert.Contains("static unsafe", cs);
    }

    // ─── ClassifyParam ─────────────────────────────────────────────────

    [Fact]
    public void ClassifyParam_Primitive_ReturnsPrimitive()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("value", new NamedTypeSpec("Swift.Int"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.Primitive, result);
    }

    [Fact]
    public void ClassifyParam_Class_ReturnsPayloadHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("obj", new NamedTypeSpec("TestModule.MyClass"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PayloadHandle, result);
    }

    [Fact]
    public void ClassifyParam_ObjCBridged_ReturnsObjCHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("error", new NamedTypeSpec("Foundation.NSError"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.ObjCHandle, result);
    }

    [Fact]
    public void ClassifyParam_NonFrozenStruct_ReturnsPayloadHandle()
    {
        // DataResponse has Flags = None (not frozen), Kind = Struct → non-frozen struct
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("req", new NamedTypeSpec("TestModule.DataResponse"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PayloadHandle, result);
    }

    [Fact]
    public void ClassifyParam_FrozenStruct_ReturnsFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("point", new NamedTypeSpec("TestModule.FrozenPoint"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.FrozenStruct, result);
    }

    [Fact]
    public void ClassifyParam_FrozenWithMemory_ReturnsFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("container", new NamedTypeSpec("TestModule.FrozenContainer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.FrozenStruct, result);
    }

    [Fact]
    public void ClassifyParam_PointerType_ReturnsPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("ptr", new NamedTypeSpec("Swift.UnsafePointer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PointerType, result);
    }

    [Fact]
    public void ClassifyParam_BufferPointerType_ReturnsPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("buf", new NamedTypeSpec("Swift.UnsafeBufferPointer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PointerType, result);
    }

    [Fact]
    public void ClassifyParam_NativeRemapped_ReturnsNativeRemapped()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("url", new NamedTypeSpec("Foundation.URL"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.NativeRemapped, result);
    }

    // ─── IsEligible: ParamAbiCategory integration ────────────────────

    [Fact]
    public void IsEligible_ResultClosure_NonFrozenStructParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (Result<MyData, MyError>) -> Void (bound generic)
        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: ImageRequest (non-frozen struct)
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_FrozenStructParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: FrozenPoint (frozen struct) — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.FrozenPoint"), "point");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_PointerStructParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: UnsafePointer — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Swift.UnsafePointer"), "ptr");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_NativeRemappedParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: Foundation.URL (native-remapped) — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Foundation.URL"), "url");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_ClassParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: MyData (Swift class) — passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.MyData"), "data");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_ObjCParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: NSError (ObjC-bridged) — passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Foundation.NSError"), "error");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── TryEmit: Non-frozen struct params ────────────────────────────

    [Fact]
    public void TryEmit_NonFrozenStructParam_EmitsPayloadDangerousGetHandle()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Non-frozen struct params use .Payload.DangerousGetHandle() (same ABI as classes)
        Assert.Contains("request.Payload.DangerousGetHandle()", cs);
    }

    [Fact]
    public void TryEmit_ResultClosureArg_EmitsWithUnsafePointer()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var csWriter = new CSharpWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Result<T,E> is a value type — needs withUnsafePointer wrapping
        Assert.Contains("withUnsafePointer(to:", swift);
    }

    [Fact]
    public void TryEmit_ResultClosureArg_EmitsSwiftMarshalForResult()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Result<T,E> closure args come as IntPtr, marshalled via SwiftMarshal.MarshalFromSwift
        // BoundGenericsHandler produces fully-qualified names
        Assert.Contains("SwiftMarshal.MarshalFromSwift<Swift.Runtime.SwiftResult<Swift.TestModule.MyData, Swift.TestModule.MyError>>", cs);
    }

    // ─── Multi-Closure (C1) ──────────────────────────────────────────

    [Fact]
    public void IsEligible_TwoClosures_OneWithBoundGeneric_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure 1: (DataResponse<MyData>) -> Void — bound generic
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2: (Int) -> Void — primitive-only
        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_TwoClosures_NeitherBoundGeneric_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Both closures have only primitives — no bound generics → not eligible
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onA", closure2, "onB");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultiClosure_AsyncClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));
        closure2.IsAsync = true; // Async closure — not supported

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultiClosure_UnsupportedArg_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2 has an unsupported arg (unknown type not in database)
        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("UnknownModule.UnknownType") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_SingleClosure_BackwardCompatible()
    {
        // Verify single-closure behavior is unchanged after multi-closure refactor
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_TwoClosures_EmitsTwoCallbacksAndFuncPtrFields()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // Two funcPtr+context pairs in Swift wrapper params
        Assert.Contains("onResultFuncPtr", swift);
        Assert.Contains("onResultContext", swift);
        Assert.Contains("onProgressFuncPtr", swift);
        Assert.Contains("onProgressContext", swift);

        // Two separate callback methods in C# (MCB_xxx and MCB_xxx_1)
        Assert.Contains("s_MCB_", cs); // funcPtr field references

        // Two GCHandle allocations in public method
        Assert.Contains("__gcHandle", cs);
        Assert.Contains("__gcHandle_1", cs);

        // Two delegate params in public method
        Assert.Contains("onResult", cs);
        Assert.Contains("onProgress", cs);
    }

    [Fact]
    public void TryEmit_BoundGenericPlusVoidClosure_EmitsValidSwiftSyntax()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure 1: (DataResponse<MyData>) -> Void — bound generic (activates bridge)
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2: () -> Void — zero-arg closure
        var closure2 = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onComplete");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();

        // Zero-arg closure must NOT produce "{ in" or "{  in" — just "{ cdecl(...) }"
        Assert.DoesNotMatch(@"\{\s+in\s", swift);
        // The bound-generic closure SHOULD have "in" (it has params)
        Assert.Contains("in", swift);
        // Both closures should appear in wrapper
        Assert.Contains("onResultFuncPtr", swift);
        Assert.Contains("onCompleteFuncPtr", swift);
    }

    // ─── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;) -> Void.
    /// DataResponse is a bound generic struct. MyData is a Swift-native class.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithBoundGenericClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;, Bool) -> Void.
    /// Tests mixed bound-generic + primitive closure args.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithMixedClosureArgs()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>, Bool) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                boundGenericArg,
                new NamedTypeSpec("Swift.Bool")
            }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onUpdate", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;) -> Bool.
    /// Tests Bool return conversion in Swift wrapper.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithBoolReturnClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Bool
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            new NamedTypeSpec("Swift.Bool"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("shouldContinue", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "predicate");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with an ObjC-bridged non-closure param + bound generic closure.
    /// Tests that ObjC params use .Handle.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithObjCParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Method: doWork(presenter: NSError, handler: closure)
        // Using NSError as ObjC-bridged param (it's registered in CreateTypeDatabase)
        var method = CreateMethodDeclWithNonClosureParam("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler",
            new NamedTypeSpec("Foundation.NSError"), "presenter");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with a Swift-native class non-closure param + bound generic closure.
    /// Tests that Swift class params use .Payload.DangerousGetHandle().
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithSwiftClassParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Method: doWork(other: MyData, handler: closure)
        // MyData is a Swift-native class
        var method = CreateMethodDeclWithNonClosureParam("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler",
            new NamedTypeSpec("TestModule.MyData"), "other");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    // ─── Type/Declaration Factory Methods ─────────────────────────────

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation module — ObjC-bridged
        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        // TestModule — user types
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
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

    /// <summary>
    /// Creates a method with a single closure parameter.
    /// </summary>
    private static MethodDecl CreateMethodDecl(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
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

    /// <summary>
    /// Creates a method with a non-closure param + a closure parameter.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithNonClosureParam(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName,
        TypeSpec nonClosureType, string nonClosureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(nonClosureParamName, nonClosureType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
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

    /// <summary>
    /// Creates a method with two closure parameters.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithTwoClosures(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType,
        ClosureTypeSpec closure1, string closure1Name,
        ClosureTypeSpec closure2, string closure2Name)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closure1Name, closure1, moduleDecl),
                CreateArgument(closure2Name, closure2, moduleDecl)
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

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a type database with extended types for classifier tests:
    /// FrozenPoint (frozen struct), FrozenContainer (frozen + memory), Foundation.URL (native-remapped).
    /// Each module has its own separate database since TypeDatabase rejects duplicate modules.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithExtendedTypes()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation module — ObjC-bridged + native-remapped
        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        // TestModule — all test types in one module
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "FrozenPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
                MetadataAccessor = "$s10TestModule11FrozenPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenContainer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "FrozenContainer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenContainer"),
                MetadataAccessor = "$s10TestModule15FrozenContainerVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    /// <summary>
    /// Creates a type database with Result-related types for eligibility/emission tests:
    /// Swift.Result, TestModule.ImageRequest (non-frozen struct), TestModule.MyError (non-simple enum),
    /// plus all extended types.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithResultTypes()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives + Result
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation module — ObjC-bridged + native-remapped
        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        // TestModule — all types including Nuke-like Result types
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "FrozenPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
                MetadataAccessor = "$s10TestModule11FrozenPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ImageRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageRequest"),
                MetadataAccessor = "$s10TestModule12ImageRequestVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
                MetadataAccessor = "$s10TestModule7MyErrorOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Non-simple enum
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }
}
