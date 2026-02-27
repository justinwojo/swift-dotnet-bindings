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
