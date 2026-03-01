// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the NestedClosureBridge emitter — handles methods with closure-in-closure
/// parameters via a two-level bridge ABI (outer C# → Swift, inner Swift → C#).
/// </summary>
public class NestedClosureBridgeTests
{
    // ─── IsEligible ───────────────────────────────────────────────────

    [Fact]
    public void IsEligible_MethodWithNestedClosure_ReturnsTrue()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_RegularClosure_ReturnsFalse()
    {
        // A closure with no inner closure — not eligible for NCB
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

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsAsync = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.Throws = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_Constructor_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultipleNestedClosures_ReturnsFalse()
    {
        // Outer closure with TWO inner closures — not supported in spike
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure 1: (Int) -> Void
        var innerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        // Inner closure 2: (Bool) -> Void
        var innerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { innerClosure1, innerClosure2 }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_InnerClosureWithNonVoidReturn_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: (Int) -> Bool (non-void return)
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            new NamedTypeSpec("Swift.Bool"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_InnerClosureWithNonCdeclArg_ReturnsFalse()
    {
        // Inner closure with String arg — String is not cdecl-compatible
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: (Swift.String) -> Void — String needs marshalling
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncOuterClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        outerClosureType.IsAsync = true;

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncInnerClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        innerClosure.IsAsync = true;

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_OptionalRefOuterArg_ReturnsFalse()
    {
        // Optional<NSObject> as outer arg — IsCdeclCompatibleType allows it
        // but our trampoline can't marshal it (would emit Unmanaged<Optional<T>>)
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        var optionalNSObject = new NamedTypeSpec("Swift.Optional",
            new[] { new NamedTypeSpec("Foundation.NSObject") });

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                optionalNSObject,
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_OptionalRefInnerArg_ReturnsFalse()
    {
        // Inner closure with Optional<NSObject> arg — not handled by trampoline
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var optionalNSObject = new NamedTypeSpec("Swift.Optional",
            new[] { new NamedTypeSpec("Foundation.NSObject") });

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { optionalNSObject }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── TryEmit: Swift Wrapper ───────────────────────────────────────

    [Fact]
    public void TryEmit_EmitsSwiftSilgenName()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        Assert.Contains("@_silgen_name(\"SBW_NCB_", swift);
    }

    [Fact]
    public void TryEmit_EmitsInnerConventionCTrampoline()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("@convention(c)", swift);
        Assert.Contains("innerTrampoline", swift);
        // Verify trampoline uses parameter names (__ip0, __closureBox), not type names
        Assert.Contains("__ip0, __closureBox in", swift);
    }

    [Fact]
    public void TryEmit_EmitsUnmanagedPassRetainedBoxing()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("Unmanaged.passRetained", swift);
        Assert.Contains("as AnyObject", swift);
    }

    [Fact]
    public void TryEmit_CallbackHasInnerFuncPtrAndContext()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Callback should have IntPtr params for inner funcPtr + context + outer context
        Assert.Contains("IntPtr innerFuncPtr", cs);
        Assert.Contains("IntPtr innerContext", cs);
        Assert.Contains("IntPtr outerContext", cs);
    }

    [Fact]
    public void TryEmit_PublicMethodHasNestedActionDelegate()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Public method should have Action<..., Action<...>> parameter type
        Assert.Contains("Action<", cs);
    }

    [Fact]
    public void TryEmit_PublicMethodConstructsInnerActionFromFuncPtr()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Callback should construct inner Action from delegate* unmanaged[Cdecl]
        Assert.Contains("delegate* unmanaged[Cdecl]", cs);
    }

    [Fact]
    public void TryEmit_PInvokeHasSwiftSelfForInstanceMethod()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        Assert.Contains("SwiftSelf", cs);
    }

    [Fact]
    public void TryEmit_DynamicSelfReturn_EmitsClassMarshal()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndDynamicSelfReturn();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // DynamicSelf return should emit NativeMemory + SwiftMarshal class pattern
        Assert.Contains("NativeMemory.Alloc", cs);
        Assert.Contains("SwiftMarshal.MarshalFromSwift", cs);
    }

    [Fact]
    public void TryEmit_SimpleEnumInnerArg_UsesCorrectSwiftScalarType()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndSimpleEnumInnerArg();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Simple enum args should use unsafeBitCast in the trampoline
        Assert.Contains("unsafeBitCast", swift);
    }

    [Fact]
    public void TryEmit_ObjCOuterArg_UsesUnmanagedPassUnretained()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndObjCOuterArg();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // ObjC outer args → Unmanaged.passUnretained().toOpaque()
        Assert.Contains("Unmanaged.passUnretained", swift);
    }

    [Fact]
    public void TryEmit_SetsWasEmitted()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(method.WasEmitted);
        Assert.True(method.UsesWrapperLibrary);
        Assert.True(method.UsesFreeFunctionWrapper);
    }

    // ─── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a method shaped like Alamofire's onHTTPResponse(on:perform:).
    /// Outer closure: (HTTPURLResponse, (ResponseDisposition) -> Void) -> Void
    /// Inner closure: (ResponseDisposition) -> Void where ResponseDisposition is a simple enum.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosure()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        // Using NSObject as a stand-in for HTTPURLResponse (ObjC-bridged)
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure that returns Self (DynamicSelf).
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndDynamicSelfReturn()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Return type: Self (DynamicSelf) — IsDynamicSelf is computed from Name == "Self"
        var returnType = new NamedTypeSpec("Self");

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            returnType, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure where the inner closure takes a simple enum arg.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndSimpleEnumInnerArg()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void (ResponseDisposition is a simple enum)
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (Int, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure where the outer closure has an ObjC-bridged arg.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndObjCOuterArg()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (Int) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    // ─── Type/Declaration Factory Methods ─────────────────────────────

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

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

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSObject"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
                MetadataAccessor = "$s10TestModule11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnumTypes()
    {
        var typeDatabase = new TypeDatabase();

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
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "UInt32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt32"),
                MetadataAccessor = "$ss6UInt32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSObject"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "DataRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
                MetadataAccessor = "$s10TestModule11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ResponseDisposition"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ResponseDisposition"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ResponseDisposition"),
                MetadataAccessor = "$s10TestModule19ResponseDispositionOMa",
                Flags = TypeRecordFlags.SimpleEnum | TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "UInt32"
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

    private static MethodDecl CreateMethodDecl(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule11DataRequestC{name.Length}{name}yACyF",
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
}
