// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for @_silgen_name → @_cdecl trampoline conversion. Verifies that all 7
/// wrapper-owned P/Invoke paths route through @_cdecl when eligible, eliminating
/// CallConvSwift from generated code.
/// </summary>
public class SilgenNameTrampolineTests
{
    #region HasCdeclCompatibleFunctionShape Tests

    [Fact]
    public void HasCdeclCompatibleFunctionShape_SimpleClassMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_GenericClassParent_ConcreteMethod_ReturnsTrue()
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

        // Generic class parent with concrete method signature → compatible via protocol erasure
        Assert.True(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_GenericMethod_ReturnsFalse()
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

        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_ActorParent_SyncMethod_ReturnsFalse()
    {
        // Synthetic: a sync method on an actor parent that somehow bypassed parser normalization.
        // The parser turns actor-isolated instance methods into async ones; a sync method here
        // has no path to dispatch safely and must be rejected.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyActor");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyActor", moduleDecl);
        parentDecl.IsActor = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_ActorParent_AsyncMethod_ReturnsTrue()
    {
        // Actor-isolated async instance methods route through the async @_cdecl wrapper —
        // Task { await self.method() } handles the executor hop automatically.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyActor");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyActor", moduleDecl);
        parentDecl.IsActor = true;
        var method = CreateMethodDecl("fetchCount", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_ClosureReturn_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureReturn = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var method = CreateMethodWithReturn("getHandler", closureReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_TupleReturn_ReturnsTrue()
    {
        // Tuple returns are now routed through IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var tupleReturn = new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int") });
        var method = CreateMethodWithReturn("getPair", tupleReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_OpaqueReturn_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var opaqueReturn = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.MyType") });
        opaqueReturn.IsOpaque = true;
        var method = CreateMethodWithReturn("getOpaque", opaqueReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    #endregion

    #region IsNestedFrozenStructParam / IsNonPrimitiveFrozenStructParam Tests

    [Fact]
    public void IsNestedFrozenStructParam_NestedFrozenStruct_ReturnsTrue()
    {
        var typeDb = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Inner"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        var arg = CreateArgument("val", new NamedTypeSpec("TestModule.Outer.Inner"));

        Assert.True(MethodWrapperEmitter.IsNestedFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNestedFrozenStructParam_NonNestedFrozenStruct_ReturnsFalse()
    {
        var typeDb = CreateTestEnvironment("MyType").typeDb;

        var arg = CreateArgument("val", new NamedTypeSpec("Swift.Int"));

        Assert.False(MethodWrapperEmitter.IsNestedFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNonPrimitiveFrozenStructParam_FrozenStruct_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.VectorAnimationColor", TypeRecordFlags.Frozen, TypeRecordKind.Struct));

        var arg = CreateArgument("color", new NamedTypeSpec("TestModule.VectorAnimationColor"));

        Assert.True(MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNonPrimitiveFrozenStructParam_PrimitiveInt_ReturnsFalse()
    {
        var typeDb = CreateTestEnvironment("MyType").typeDb;
        var arg = CreateArgument("x", new NamedTypeSpec("Swift.Int"));

        Assert.False(MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNonPrimitiveFrozenStructParam_String_ReturnsFalse()
    {
        var typeDb = CreateTestEnvironment("MyType").typeDb;
        var arg = CreateArgument("name", new NamedTypeSpec("Swift.String"));

        Assert.False(MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNonPrimitiveFrozenStructParam_AppleValueType_ReturnsFalse()
    {
        // Foundation.Date is a known Apple value type (blittable Double wrapper).
        // It should be allowed through @_cdecl wrappers despite being a frozen struct.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
        typeDb.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "Date"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
                MetadataAccessor = "$s10Foundation4DateVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(foundationModule);

        var arg = CreateArgument("date", new NamedTypeSpec("Foundation.Date"));

        Assert.False(MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(arg, typeDb));
    }

    [Fact]
    public void IsNonPrimitiveFrozenStructParam_CustomFrozenStruct_ReturnsTrue()
    {
        // Custom frozen structs must still be blocked —
        // they trigger "Swift structs cannot be represented in Objective-C" at wrapper compilation.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.CustomColor", TypeRecordFlags.Frozen, TypeRecordKind.Struct));

        var arg = CreateArgument("color", new NamedTypeSpec("TestModule.CustomColor"));

        Assert.True(MethodWrapperEmitter.IsNonPrimitiveFrozenStructParam(arg, typeDb));
    }

    #endregion

    #region Closure Wrapper @_cdecl Conversion Tests

    [Fact]
    public void ClosureWithCdeclWrapper_PrimitiveParam_UsesCdecl()
    {
        // When AsyncLibraryName is set, closures go through the STANDARD method wrapper
        // path (ShouldEmitWrapper guard 8 allows them). The standard wrapper handles
        // closures inline via HasClosureParams.
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handlePrim", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Standard method wrapper @_cdecl (not separate closure wrapper)
        Assert.Contains("@_cdecl", swiftOutput);
        // C# P/Invoke must use Cdecl calling convention
        Assert.Contains("CallConvCdecl", csOutput);
        // Must NOT use CallConvSwift
        Assert.DoesNotContain("CallConvSwift", csOutput);
        // Method gets standard wrapper flags, not closure-specific wrapper flags
        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.True(method.HasClosureParams);
    }

    [Fact]
    public void ClosureWithCdeclWrapper_ClassParam_UsesCdecl()
    {
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleCls", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));
        method.CSSignature.Add(CreateArgument("data", new NamedTypeSpec("TestModule.Loader"), moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // @_cdecl with class param converted via Unmanaged
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
        Assert.Contains("CallConvCdecl", csOutput);
    }

    [Fact]
    public void ClosureWrapper_GenericParent_NoConversion()
    {
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("GenericLoader", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleGen", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Generic class parent with concrete closure signature → @_cdecl with protocol erasure
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("private protocol _SBW_P_", swiftOutput);
    }

    [Fact]
    public void ClosureWithCdeclWrapper_ThrowingMethod_UsesCdeclWithErrorOut()
    {
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleThrow", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: true,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // @_cdecl with errorOut for throwing method
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("errorOut", swiftOutput);
        Assert.Contains("Unmanaged.passRetained(error as AnyObject).toOpaque()", swiftOutput);
        Assert.Contains("CallConvCdecl", csOutput);
    }

    #endregion

    #region Optional Pointer Wrapper @_cdecl Conversion Tests

    [Fact]
    public void OptionalPointerWrapper_LargeOptionalWithPrimitive_UsesCdecl()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Must use @_cdecl, not @_silgen_name
        Assert.Contains("@_cdecl", swiftOutput);
        // Must use UnsafeRawPointer for large optional param
        Assert.Contains("UnsafeRawPointer", swiftOutput);
        // C# must use Cdecl
        Assert.Contains("CallConvCdecl", csOutput);
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    [Fact]
    public void OptionalPointerWrapper_GenericParent_NoWrapper()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("processGen", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Generic parent with concrete signature → method gets @_cdecl wrapper via protocol erasure.
        // Previously blocked by frozen struct gate (Optional is registered as frozen struct),
        // now handled correctly via UnsafeRawPointer. No optional pointer wrapper needed.
        Assert.False(method.HasOptionalPointerWrapper);
        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("_SBW_P_", swiftOutput); // Protocol erasure
    }

    [Fact]
    public void OptionalPointerWrapper_ActorParent_NoWrapper()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyActor", moduleDecl);
        parentDecl.IsActor = true;

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("processActor", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        EmitMethod(method, typeDatabase);

        // Actor parent → no @_cdecl (actor-isolated methods require async context)
        Assert.False(method.UsesCdeclMethodWrapper);
    }

    [Fact]
    public void OptionalPointerWrapper_ThrowingWithLargeOptional_UsesCdeclWithRetainedError()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("tryProcess", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: true,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // @_cdecl with retained error object pattern
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("errorOut", swiftOutput);
        Assert.Contains("Unmanaged.passRetained(error as AnyObject).toOpaque()", swiftOutput);
        Assert.Contains("CallConvCdecl", csOutput);
    }

    #endregion

    #region Async @_cdecl Conversion Tests

    [Fact]
    public void Async_NonGenericWithPrimitive_UsesCdecl()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var method = CreateMethodDecl("fetchCount", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("limit", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Async must use @_cdecl
        Assert.Contains("@_cdecl", swiftOutput);
        // Must use UnsafeMutableRawPointer for self, not OpaquePointer
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
        Assert.Contains("CallConvCdecl", csOutput);
    }

    [Fact]
    public void Async_GenericMethod_NoAsyncWrapper()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var method = CreateMethodDecl("fetch", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Method-own generic parameters can't be used in @convention(c) callbacks,
        // so no async Swift wrapper is emitted (neither @_silgen_name nor @_cdecl).
        Assert.DoesNotContain("@_silgen_name", swiftOutput);
        Assert.DoesNotContain("@_cdecl", swiftOutput);
    }

    [Fact]
    public void Async_ActorParent_UsesCdecl()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyActor", moduleDecl);
        parentDecl.IsActor = true;

        var method = CreateMethodDecl("fetchCount", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Actor parent with async instance method → routed through @_cdecl async wrapper.
        // Task { await self.method() } hops to the actor's executor automatically,
        // unblocking SB0001 fallbacks for sync-on-actor APIs on custom-actor-isolated types.
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.DoesNotContain("@_silgen_name", swiftOutput);
    }

    [Fact]
    public void Async_InstanceMethod_SelfAsUnsafeMutableRawPointer()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var method = CreateMethodDecl("fetchData", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Instance async method with @_cdecl: self as UnsafeMutableRawPointer
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
        // Should NOT use OpaquePointer (legacy path)
        Assert.DoesNotContain("OpaquePointer", swiftOutput);
    }

    [Fact]
    public void Async_WithClosureParam_NoConversion_SkippedAsAbiUnsafe()
    {
        // An async method whose signature carries a non-baseline closure parameter is rejected
        // by HasCdeclCompatibleFunctionShape (no @_cdecl wrapper produced). Previously the
        // legacy path emitted an `@_silgen_name` Swift trampoline plus a CallConvSwift P/Invoke
        // into Swift's async ABI — genuinely ABI-unsafe at runtime: closure ownership transfer
        // needs the destroy-thunk projection that lives only on the cdecl-wrapped path.
        // WrapperValidation.IsSkippedWrapperDirectPInvoke recognises this shape
        // (async + closure param) and skips the method with an
        // "ABI-unsafe direct call" diagnostic instead of emitting a working-looking-but-broken
        // API. The mirror unit-level test lives in
        // AbiSafetyTests.IsSkippedWrapperDirectPInvoke_AsyncWithClosureParam_ReturnsTrue.
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("fetchWithCallback", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Skip path: no Swift trampoline, no C# P/Invoke, an Unsupported comment in C#.
        Assert.DoesNotContain("@_silgen_name", swiftOutput);
        Assert.DoesNotContain("@_cdecl", swiftOutput);
        Assert.DoesNotContain("CallConvSwift", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("ABI-unsafe", csOutput);
    }

    #endregion

    #region Wrapper Ownership Gate Tests

    [Fact]
    public void WrapperOwnership_ClosureWrapper_NoDoubleEmission()
    {
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleOwnership", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // The standard method wrapper emits one @_cdecl for the method.
        // Infrastructure helpers (Utf8Slice, etc.) may also appear as @_cdecl.
        // Verify the METHOD's @_cdecl appears exactly once — count by symbol prefix.
        var methodCdeclCount = CountOccurrences(swiftOutput, "SBW_TestModule_Loader_handleOwnership");
        Assert.Equal(1, methodCdeclCount);
    }

    [Fact]
    public void WrapperOwnership_OptionalPointerWrapper_NoDoubleEmission()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Verify the METHOD's @_cdecl appears exactly once — count by symbol prefix.
        var methodCdeclCount = CountOccurrences(swiftOutput, "SBW_TestModule_Processor_process");
        Assert.Equal(1, methodCdeclCount);
    }

    #endregion

    #region C# P/Invoke Convention Tests

    [Fact]
    public void PInvoke_ClosureWrapperCdecl_UsesCdeclConvention()
    {
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handlePInvoke", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("CallConvCdecl", csOutput);
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    [Fact]
    public void PInvoke_OptionalPointerWrapperCdecl_UsesCdeclConvention()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("CallConvCdecl", csOutput);
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    [Fact]
    public void PInvoke_AsyncCdecl_UsesCdeclConvention()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var method = CreateMethodDecl("fetchCount", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("CallConvCdecl", csOutput);
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    #endregion

    #region CanConvertToCdecl Eligibility Tests

    [Fact]
    public void ClosureCanConvertToCdecl_EligibleMethod_ReturnsTrue()
    {
        // CanConvertToCdecl checks function-level shape + per-param guards.
        // It's used when the closure wrapper path triggers (no AsyncLibraryName).
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleElig", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // Simulate wrapper setup (closure wrapper sets these)
        method.HasClosureCdeclWrapper = true;
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;

        var env = new MethodEnvironment(method, typeDatabase);
        Assert.True(ClosureEmitter.CanConvertToCdecl(env));
    }

    [Fact]
    public void OptionalPointerCanConvertToCdecl_EligibleMethod_ReturnsTrue()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        method.HasOptionalPointerWrapper = true;
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;

        var env = new MethodEnvironment(method, typeDatabase);
        Assert.True(OptionalPointerWrapperEmitter.CanConvertToCdecl(env));
    }

    #endregion

    #region End-to-End Flag Tests

    [Fact]
    public void Emit_ClosureWithCdeclWrapper_SetsUsesCdeclMethodWrapper()
    {
        // When AsyncLibraryName is set, closures go through the standard method wrapper
        // (ShouldEmitWrapper guard 8 allows them). HasClosureParams is set, not HasClosureCdeclWrapper.
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleFlags", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        EmitMethod(method, typeDatabase);

        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.True(method.HasClosureParams);
        Assert.True(method.UsesCdeclWrapper); // Computed property
    }

    [Fact]
    public void Emit_OptionalPointerWrapperCdecl_SetsUsesCdeclMethodWrapper()
    {
        var typeDatabase = CreateOptionalPointerTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        EmitMethod(method, typeDatabase);

        // With frozen struct gate removed, ShouldEmitWrapper returns true first,
        // so method gets standard @_cdecl wrapper (not optional pointer wrapper).
        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.False(method.HasOptionalPointerWrapper);
        Assert.True(method.UsesCdeclWrapper);
    }

    [Fact]
    public void Emit_AsyncCdecl_SetsUsesCdeclMethodWrapper()
    {
        var typeDatabase = CreateAsyncTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Fetcher", moduleDecl);

        var method = CreateMethodDecl("fetchCount", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: true, throws: false,
            methodType: MethodType.Instance);

        EmitMethod(method, typeDatabase);

        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.True(method.UsesCdeclWrapper);
    }

    [Fact]
    public void Emit_ClosureOnGenericParent_UsesCdeclMethodWrapper()
    {
        // Generic class parent with concrete closure signature → gets @_cdecl via protocol erasure
        var typeDatabase = CreateClosureTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("GenericLoader", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handleGenFlag", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        EmitMethod(method, typeDatabase);

        Assert.True(method.UsesCdeclMethodWrapper);
    }

    #endregion

    #region Simple Enum @_cdecl Return Tests

    [Fact]
    public void MethodWrapper_RawValueEnumReturn_EmitsRawValueConversion()
    {
        // Regression test: @_cdecl wrapper paths must convert simple enum returns
        // via .rawValue, not return the Swift enum directly (which is incompatible with
        // the C integer return type in the @_cdecl signature).
        // With frozen struct gate removed, method goes through standard @_cdecl method wrapper.
        var typeDatabase = CreateOptionalPointerTypeDatabaseWithEnum("Status", rawValueType: "Int32");

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("getStatus", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Status"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Method now goes through standard @_cdecl wrapper (not optional pointer wrapper)
        Assert.True(method.UsesCdeclMethodWrapper,
            $"UsesCdeclMethodWrapper=false. HasOptionalPointerWrapper={method.HasOptionalPointerWrapper}, UsesWrapperLibrary={method.UsesWrapperLibrary}. Swift:\n{swiftOutput}");
        Assert.Contains("@_cdecl", swiftOutput);
        // Must convert via .rawValue, not return the enum directly
        Assert.Contains(".rawValue)", swiftOutput);
    }

    [Fact]
    public void MethodWrapper_TagOnlyEnumReturn_EmitsSafeCopyMemoryPattern()
    {
        // Regression test: tag-only enums (no raw value) must use safe copyMemory
        // pattern instead of load(as: Int.self) which reads 8 bytes from a 1-byte value.
        // With frozen struct gate removed, method goes through standard @_cdecl method wrapper.
        var typeDatabase = CreateOptionalPointerTypeDatabaseWithEnum("Direction", rawValueType: null);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("getDirection", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Direction"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("name", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("@_cdecl", swiftOutput);
        Assert.True(method.UsesCdeclMethodWrapper);
        // Tag-only enum: must use safe copyMemory widening, not load(as: Int.self).
        // The transport scalar is 32-bit Int32 (matching the C# `int` P/Invoke side —
        // the int↔Int width contract pinned by EnumAbiWidthConsistencyTests), zero-init'd
        // and copyMemory-widened from the enum's actual (usually 1-byte) allocation.
        Assert.Contains("let resultSize = MemoryLayout.size(ofValue: result)", swiftOutput);
        Assert.Contains("var tag: Int32 = 0", swiftOutput);
        Assert.Contains("copyMemory", swiftOutput);
        Assert.Contains("byteCount: resultSize", swiftOutput);
        Assert.DoesNotContain("load(as: Int.self)", swiftOutput);
    }

    #endregion

    #region Test Helpers

    private static TypeDatabase CreateClosureTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.GenericLoader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "GenericLoader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GenericLoader"),
                MetadataAccessor = "$s10TestModule13GenericLoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    /// <summary>
    /// Creates an optional-pointer type database with an additional enum type for return type tests.
    /// Exercises EmitCdeclDirectReturn through the OptionalPointerWrapperEmitter path.
    /// </summary>
    private static TypeDatabase CreateOptionalPointerTypeDatabaseWithEnum(string enumName, string? rawValueType)
    {
        var freshDb = new TypeDatabase();
        freshDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
        freshDb.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
                MetadataAccessor = "$s10TestModule9ProcessorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", enumName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
                MetadataAccessor = $"$s10TestModule{enumName.Length}{enumName}OMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType
            });
        freshDb.AddModuleDatabase(module);

        return freshDb;
    }

    private static TypeDatabase CreateOptionalPointerTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
                MetadataAccessor = "$s10TestModule9ProcessorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyActor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyActor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyActor"),
                MetadataAccessor = "$s10TestModule7MyActorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateAsyncTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Fetcher"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher"),
                MetadataAccessor = "$s10TestModule7FetcherCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyActor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyActor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyActor"),
                MetadataAccessor = "$s10TestModule7MyActorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static void RegisterSwiftCoreTypes(ModuleTypeDatabase swiftModule)
    {
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
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
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterSwiftCoreTypes(swiftModule);
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
        foreach (var (qualifiedName, flags, kind) in extraTypes)
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
                    Kind = kind
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
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
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
            IsSynthesizedAccessor = false
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
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        bool isAsync,
        bool throws,
        MethodType methodType)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name.Length}{name}SiyF",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl? moduleDecl = null)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        // Use a fresh ModuleEmissionContext per test to avoid dedup interference
        // (the default TypeHandlerContext.Empty uses a static singleton).
        var ctx = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, ctx);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    #endregion
}
