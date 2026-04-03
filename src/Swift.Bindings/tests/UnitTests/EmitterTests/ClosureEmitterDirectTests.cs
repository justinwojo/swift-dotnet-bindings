// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Direct tests for ClosureEmitter static methods — EmitClosureReturnMarshalling
/// and EmitEscapingClosureCallback in Swift vs Cdecl calling conventions.
/// </summary>
public class ClosureEmitterDirectTests
{
    [Fact]
    public void EmitClosureReturnMarshalling_NonVoidReturn_EmitsEscapingClosure()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("SwiftEscapingClosure", result);
        Assert.Contains("FromSwift", result);
        Assert.Contains("result.FunctionPointer", result);
        Assert.Contains("result.Context", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_EmitsCallConvSwift()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("CallConvSwift", result);
        Assert.Contains("SwiftSelf context", result);
        Assert.Contains("[UnmanagedCallersOnly(CallConvs", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_CdeclMode_EmitsCallConvCdecl()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: true);

        var result = output.ToString();
        Assert.Contains("CallConvCdecl", result);
        Assert.Contains("IntPtr contextPtr", result);
        Assert.DoesNotContain("SwiftSelf", result);
    }

    #region Q3 — Class/ObjC return + Optional<ObjC> regression tests

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_ClassReturn_EmitsDangerousGetHandle()
    {
        // Gap #1: useCdecl=false with class-returning closure must use DangerousGetHandle
        var typeDatabase = CreateTypeDatabaseWithReferenceTypes();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: () -> Loader
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("TestModule.Loader"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "getLoader", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule9getLoaderyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("CallConvSwift", result);
        Assert.Contains("DangerousGetHandle", result);
        Assert.DoesNotContain("NativeMemory.Alloc", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_ObjCReturn_EmitsHandle()
    {
        // Gap #1: useCdecl=false with ObjC-returning closure must use .Handle
        var typeDatabase = CreateTypeDatabaseWithReferenceTypes();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: () -> NSError
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Foundation.NSError"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "getError", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule8getErroryyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("CallConvSwift", result);
        Assert.Contains(".Handle", result);
        Assert.DoesNotContain("DangerousGetHandle", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_OptionalObjCParam_EmitsNullCheck()
    {
        // Gap #2: Optional<ObjC-bridged> parameter must null-check and use GetNSObject
        var typeDatabase = CreateTypeDatabaseWithReferenceTypes();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Optional<NSError>) -> Void
        var optionalNSError = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Foundation.NSError"));
        var closureTypeSpec = new ClosureTypeSpec(optionalNSError, TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "handle", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6handleyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("!= null", result);
        Assert.Contains("GetNSObject", result);
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalObjCParam_ReturnsTrue()
    {
        // Gap #2 symmetry: Optional<ObjC> must be Cdecl-compatible (nil-pointer ABI)
        var typeDatabase = CreateTypeDatabaseWithReferenceTypes();
        var closureHandler = new ClosureHandler(typeDatabase);

        var optionalNSError = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Foundation.NSError"));
        var closureType = new ClosureTypeSpec(optionalNSError, TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void SwiftWrapper_OptionalObjCParam_UsesOptionalPointerType()
    {
        // Gap #2 symmetry: Optional<ObjC> Swift wrapper uses UnsafeMutableRawPointer?
        var typeDatabase = CreateTypeDatabaseWithReferenceTypes();
        var closureHandler = new ClosureHandler(typeDatabase);

        var optionalNSError = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Foundation.NSError"));
        var closureType = new ClosureTypeSpec(optionalNSError, TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureType, closureHandler);
        Assert.Contains("UnsafeMutableRawPointer?", conventionCType);
    }

    #endregion

    private static TypeDatabase CreateTypeDatabaseWithSwiftInt()
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    /// <summary>
    /// Type database with Swift primitives, Optional, a class (Loader), and ObjC-bridged (NSError).
    /// Used by Q3 regression tests.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithReferenceTypes()
    {
        var typeDatabase = new TypeDatabase();

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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

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
        typeDatabase.AddModuleDatabase(testModule);

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

        return typeDatabase;
    }

    #region Tuple existential + simple enum conversion

    [Fact]
    public void EmitEscapingClosureCallback_TupleParamWithExistentialAndEnum_EmitsBothCasts()
    {
        // Closure: ((any ImageProcessing, StatusEnum)) -> Void
        // Callback receives ValueTuple<ExistentialContainer1, int> → must convert to (IImageProcessing, StatusEnum)
        var typeDatabase = CreateTypeDatabaseWithProtocolAndSimpleEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var enumElement = new NamedTypeSpec("TestModule.StatusEnum");
        var tupleParam = new TupleTypeSpec(new List<TypeSpec> { existentialElement, enumElement });
        var closureTypeSpec = new ClosureTypeSpec(tupleParam, TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "process", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7processyyF", useCdecl: false);

        var result = output.ToString();
        // Existential element should be wrapped with proxy constructor
        Assert.Contains("new ImageProcessingProxy(", result);
        // Simple enum element should be cast from underlying int to enum type (namespace-qualified)
        Assert.Contains("(TestModule.StatusEnum)", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_TupleReturnWithExistentialAndEnum_EmitsBothConversions()
    {
        // Closure: () -> (any ImageProcessing, StatusEnum)
        // Delegate returns (IImageProcessing, StatusEnum), callback returns ValueTuple<EC1, int>
        var typeDatabase = CreateTypeDatabaseWithProtocolAndSimpleEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var enumElement = new NamedTypeSpec("TestModule.StatusEnum");
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec> { existentialElement, enumElement });
        var closureTypeSpec = new ClosureTypeSpec(null, tupleReturn);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "getResult", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule9getResultyyF", useCdecl: false);

        var result = output.ToString();
        // Existential return should extract container via ExistentialContainerFactory
        Assert.Contains("ExistentialContainerFactory.GetOrCreate", result);
        // Simple enum return should cast to underlying type
        Assert.Contains("(int)", result);
    }

    [Fact]
    public void EmitClosureReturnMarshalling_TupleWithExistentialAndEnum_EmitsBothConversions()
    {
        // Invoker direction: P/Invoke returns ValueTuple<EC1, int>, delegate returns (IImageProcessing, StatusEnum)
        var typeDatabase = CreateTypeDatabaseWithProtocolAndSimpleEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var enumElement = new NamedTypeSpec("TestModule.StatusEnum");
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec> { existentialElement, enumElement });
        var closureTypeSpec = new ClosureTypeSpec(null, tupleReturn);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        // Existential should be wrapped with proxy constructor
        Assert.Contains("new ImageProcessingProxy(", result);
        // Simple enum should be cast from underlying int to enum type (namespace-qualified)
        Assert.Contains("(TestModule.StatusEnum)", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolAndSimpleEnum()
    {
        var typeDatabase = new TypeDatabase();

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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Register protocol
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IImageProcessing"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        // Register simple enum with Int raw value
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.StatusEnum"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "StatusEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StatusEnum"),
                MetadataAccessor = "$s10TestModule10StatusEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Swift.Int"
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion

    #region Class/ObjC argument handle extraction

    [Fact]
    public void EmitClosureReturnMarshalling_ClassArg_ExtractsPayloadHandle()
    {
        // When a C# closure invokes a Swift function pointer with a class argument,
        // the class handle must be extracted as void* via .Payload.DangerousGetHandle()
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Loader) -> Void — class param in Swift function pointer needs void*
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Loader"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshalling(
            csWriter, closureTypeSpec, closureHandler);

        var result = output.ToString();
        Assert.Contains("Payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void EmitClosureReturnMarshalling_ObjCArg_ExtractsHandle()
    {
        // When a C# closure invokes a Swift function pointer with an ObjC-bridged argument,
        // the handle must be extracted as void* via .Handle
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (NSError) -> Void — ObjC param in Swift function pointer needs void*
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Foundation.NSError"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshalling(
            csWriter, closureTypeSpec, closureHandler);

        var result = output.ToString();
        Assert.Contains(".Handle", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithClassAndObjC()
    {
        var typeDatabase = new TypeDatabase();

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
        typeDatabase.AddModuleDatabase(swiftModule);

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
        typeDatabase.AddModuleDatabase(testModule);

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

        return typeDatabase;
    }

    #endregion

    #region Complex enum heap deallocation (1.1 heap leak fix)

    [Fact]
    public void SwiftClosureAdapter_ComplexEnumArg_EmitsHeapAllocWithoutDefer()
    {
        // Complex enum closure args use heap allocation (__heap_N). No defer —
        // C# takes ownership via SwiftSafeHandle (VWT Destroy + NativeMemory.Free).
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (LoadingState) -> Void — complex enum arg triggers heap alloc
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.LoadingState"),
            TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "callback", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        Assert.Contains("__heap_0 = UnsafeMutableRawPointer.allocate", result);
        Assert.Contains("__heap_0.initializeMemory", result);
        Assert.DoesNotContain("defer", result);
        Assert.DoesNotContain("deallocate()", result);
    }

    [Fact]
    public void SwiftClosureAdapter_ComplexEnumArg_WithReturn_EmitsHeapAllocWithoutDefer()
    {
        // Complex enum arg with a return value: no defer (C# owns the heap memory).
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (LoadingState) -> Int32 — complex enum param + primitive return
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.LoadingState"),
            new NamedTypeSpec("Swift.Int32"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "transform", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        Assert.Contains("__heap_0", result);
        Assert.Contains("initializeMemory", result);
        Assert.DoesNotContain("defer", result);
        Assert.Contains("return", result);
    }

    [Fact]
    public void SwiftClosureAdapter_MultipleComplexEnumArgs_EmitsHeapAllocWithoutDefer()
    {
        // Multiple complex enum args: each __heap_N allocated without defer.
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (LoadingState, LoadingState) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("TestModule.LoadingState"),
                new NamedTypeSpec("TestModule.LoadingState")
            }),
            TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "handler", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        Assert.Contains("__heap_0", result);
        Assert.Contains("__heap_1", result);
        Assert.DoesNotContain("defer", result);
    }

    [Fact]
    public void SwiftClosureAdapter_ThrowingWithComplexEnumArg_EmitsHeapAllocWithoutDefer()
    {
        // Throwing closure with complex enum arg: no defer (C# owns the heap memory).
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (LoadingState) throws -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.LoadingState"),
            TupleTypeSpec.Empty) { Throws = true };
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "callback", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        Assert.Contains("__heap_0", result);
        Assert.DoesNotContain("defer", result);
        Assert.Contains("errorPtr", result);
    }

    [Fact]
    public void SwiftClosureAdapter_NoPrimitiveArgs_NoDeferEmitted()
    {
        // Primitive-only closure args should NOT have any heap allocation or defer.
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Int32) -> Void — no complex enums
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int32"),
            TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "callback", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        Assert.DoesNotContain("__heap_", result);
        Assert.DoesNotContain("deallocate", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithComplexEnum()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Complex enum: Kind=Enum, no SimpleEnum flag
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.LoadingState"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LoadingState"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LoadingState"),
                MetadataAccessor = "$s10TestModule12LoadingStateOMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion

    #region Enum rawValue Int/Int64 cast (StripePayments regression fix)

    [Fact]
    public void SwiftClosureAdapter_SimpleEnumArg_RawValueWrappedInScalarCast()
    {
        // Simple enum with rawValueType "Swift.Int" gets swiftScalar "Int64",
        // but .rawValue returns Swift's Int type. The adapter must emit
        // Int64(p0.rawValue) — not bare p0.rawValue — because Swift treats
        // Int and Int64 as distinct types.
        var typeDatabase = CreateTypeDatabaseWithSimpleEnum();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (ActionStatus) -> Void — simple enum arg
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.ActionStatus"),
            TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "callback", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        // Must wrap .rawValue in scalar type cast — not bare .rawValue.
        // "Int"-backed enums get swiftScalar "Int64" but .rawValue returns Swift.Int,
        // which is a distinct type. The cast makes it explicit.
        Assert.Contains("Int64(p0.rawValue)", result);
        Assert.DoesNotContain(" p0.rawValue,", result); // No bare .rawValue without cast
    }

    private static TypeDatabase CreateTypeDatabaseWithSimpleEnum()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ActionStatus"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ActionStatus"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ActionStatus"),
                MetadataAccessor = "$s10TestModule12ActionStatusOMa",
                Flags = TypeRecordFlags.SimpleEnum | TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int"  // ABI JSON uses unqualified names
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion

    #region ObjC-bridged struct closure parameter (IndexPath fix)

    [Fact]
    public void SwiftClosureAdapter_ObjCBridgedStructArg_EmitsAsAnyObject()
    {
        // ObjC-bridged struct types (e.g., IndexPath) need `as AnyObject` before Unmanaged
        // because Unmanaged requires a class type, and IndexPath is a struct in Swift.
        var typeDatabase = CreateTypeDatabaseWithObjCBridgedStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (SwipeAction, IndexPath) -> Void — IndexPath is an ObjC-bridged struct
        var closureTypeSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> {
                new NamedTypeSpec("TestModule.SwipeAction"),
                new NamedTypeSpec("Foundation.IndexPath") }),
            TupleTypeSpec.Empty);

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "handler", closureTypeSpec, closureHandler, isOptional: true);

        var result = string.Join("\n", lines);

        // SwipeAction (native class) should use Unmanaged directly
        Assert.Contains("Unmanaged.passUnretained(p0).toOpaque()", result);

        // IndexPath (ObjC-bridged struct) should use `as AnyObject` before Unmanaged
        Assert.Contains("Unmanaged.passUnretained(p1 as AnyObject).toOpaque()", result);
    }

    [Fact]
    public void SwiftClosureAdapter_ObjCBridgedStructReturn_UsesIndirectReturn()
    {
        // ObjC-bridged struct return types use indirect return marshalling (buffer-based),
        // not Unmanaged, because they have RequiresMemoryManagement flag.
        var typeDatabase = CreateTypeDatabaseWithObjCBridgedStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: () -> IndexPath — ObjC-bridged struct return
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Foundation.IndexPath"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "callback", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);
        // Indirect return uses buffer-based marshalling, not Unmanaged
        Assert.Contains("resultBuf", result);
        Assert.Contains("assumingMemoryBound(to: Foundation.IndexPath.self).move()", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithObjCBridgedStruct()
    {
        var typeDatabase = new TypeDatabase();

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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Native Swift class
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.SwipeAction"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SwipeAction"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwipeAction"),
                MetadataAccessor = "$s10TestModule11SwipeActionCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        // ObjC-bridged struct: kind="class" + ObjCBridged flag (matches FoundationDatabase.xml)
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.IndexPath"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSIndexPath"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.IndexPath"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        return typeDatabase;
    }

    #endregion

    #region Frozen struct closure return (CGSize/CGPoint/CGFloat direct return fix)

    [Fact]
    public void SwiftConventionCType_FrozenStructReturn_UsesIndirectReturn()
    {
        // @convention(c) cannot return Swift struct types (even frozen ones).
        // Frozen struct returns use the indirect return path: result buffer as first param, Void return.
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (CGFloat) -> CGSize — frozen struct param + frozen struct return
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGFloat"),
            new NamedTypeSpec("CoreGraphics.CGSize"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureTypeSpec, closureHandler);

        // Return type should be Void (indirect return via buffer param)
        Assert.Contains("-> Void", conventionCType);
        // First param should be the result buffer
        Assert.Contains("UnsafeMutableRawPointer", conventionCType);
    }

    [Fact]
    public void SwiftClosureAdapter_FrozenStructReturn_UsesIndirectReturn()
    {
        // The Swift adapter closure should use indirect return for frozen structs:
        // allocate a result buffer, pass to cdecl, load result back.
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (CGFloat) -> CGSize
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGFloat"),
            new NamedTypeSpec("CoreGraphics.CGSize"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var lines = ClosureEmitter.GetSwiftClosureAdapterCode(
            "block", closureTypeSpec, closureHandler, isOptional: false);

        var result = string.Join("\n", lines);

        // Should have -> CGSize return type (module-qualified) in the adapted closure
        Assert.Contains("-> CoreGraphics.CGSize", result);
        // Should use indirect return (result buffer + move for proper ARC ownership transfer)
        Assert.Contains("resultBuf", result);
        Assert.Contains("assumingMemoryBound(to:", result);
    }

    [Fact]
    public void SwiftConventionCType_FrozenStructReturn_PrimitiveParamPassesDirectly()
    {
        // Frozen struct return with a primitive param: the param should pass through
        // while the return type uses indirect return (Void).
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Int32) -> CGPoint
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int32"),
            new NamedTypeSpec("CoreGraphics.CGPoint"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureTypeSpec, closureHandler);

        Assert.Contains("Int32", conventionCType);
        // Frozen struct return uses indirect path (Void return, not CGPoint)
        Assert.Contains("-> Void", conventionCType);
    }

    [Fact]
    public void EmitEscapingClosureCallback_FrozenStructReturn_ReturnsViaPointer()
    {
        // C# callback for a frozen struct return with useCdecl should use indirect return:
        // - resultBuffer parameter (first param, IntPtr)
        // - void return type
        // - MarshalToSwift writes directly to resultBuffer (no NativeMemory.Alloc)
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (CGFloat) -> CGSize
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("CoreGraphics.CGFloat"),
            new NamedTypeSpec("CoreGraphics.CGSize"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "init", "block", closureTypeSpec, closureHandler,
            "$s6Lottie17SizeValueProviderCyAA_XCTF", useCdecl: true);

        var result = output.ToString();
        // Indirect return: callback accepts resultBuffer as first param, returns void, writes to buffer
        Assert.Contains("IntPtr resultBuffer", result);
        Assert.Contains("void init_block_", result); // void return type
        Assert.Contains("MarshalToSwift", result);
        Assert.Contains("resultBuffer", result);
        // Should NOT allocate its own buffer — writes to caller-provided buffer
        Assert.DoesNotContain("NativeMemory.Alloc", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithFrozenStruct()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var cgModule = new ModuleTypeDatabase("CoreGraphics", "/usr/lib/swift/libswiftCoreGraphics.dylib");
        // CGFloat — frozen struct, no memory management (blittable, direct return)
        cgModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGFloat"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGFloat"),
                MetadataAccessor = "$s14CoreGraphics7CGFloatVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        // CGSize — frozen struct, no memory management (direct return)
        cgModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGSize"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "CGSize"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGSize"),
                MetadataAccessor = "$sSo6CGSizeVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        // CGPoint — frozen struct, no memory management (direct return)
        cgModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "CGPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
                MetadataAccessor = "$sSo7CGPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(cgModule);

        return typeDatabase;
    }

    #endregion

    #region Session 4 — GCHandle lifetime: Free in callback, not in calling method's finally block

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_FreesGCHandleInCallback()
    {
        // Escaping closures should free the GCHandle inside the callback trampoline,
        // not in the calling method's finally block (which fires before the callback).
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        // Callback should NOT free the GCHandle — caller's finally block handles it.
        // Escaping closures may fire multiple times during a synchronous P/Invoke call.
        Assert.DoesNotContain("GCHandle.FromIntPtr(", result);
        Assert.DoesNotContain(".Free()", result);
        Assert.DoesNotContain("finally", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_CdeclMode_DoesNotFreeGCHandle()
    {
        // Cdecl escaping callback should NOT free GCHandle (caller handles it).
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: true);

        var result = output.ToString();
        Assert.DoesNotContain("GCHandle.FromIntPtr(", result);
        Assert.DoesNotContain("finally", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_VoidReturn_DoesNotFreeGCHandle()
    {
        // Void-returning escaping closures should NOT free GCHandle in callback.
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        Assert.DoesNotContain("GCHandle.FromIntPtr(", result);
        Assert.DoesNotContain("finally", result);
    }

    [Fact]
    public void EmitThrowingClosureCallback_DoesNotFreeGCHandle()
    {
        // Throwing escaping closures should NOT free GCHandle in callback.
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) throws -> Int  (escaping + throwing)
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        closureTypeSpec.Throws = true;

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitThrowingClosureCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        Assert.DoesNotContain("GCHandle.FromIntPtr(", result);
        Assert.DoesNotContain("finally", result);
    }

    [Fact]
    public void ClosureProjection_EscapingParameterPlan_NoCleanupStatements()
    {
        // Escaping closures: GCHandle intentionally leaked — Swift may store the closure
        // beyond the P/Invoke return (e.g., EventHandler.onComplete for later fire()).
        var argProjections = new List<ITypeProjection> { new BlittableProjection("nint") };
        var returnProjection = new BlittableProjection("nint");
        var projection = new ClosureProjection(
            argProjections, returnProjection,
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "callback_doWork_completion");

        var plan = projection.GetParameterPlan("completion");

        // Setup should still have GCHandle.Alloc + SwiftClosureData
        Assert.Equal(2, plan.SetupStatements.Count);
        Assert.Contains("GCHandle.Alloc", ((MarshalStatement.Line)plan.SetupStatements[0]).Code);
        Assert.Contains("SwiftClosureData", ((MarshalStatement.Line)plan.SetupStatements[1]).Code);

        // Cleanup should be EMPTY — escaping closures are intentionally leaked
        Assert.Empty(plan.CleanupStatements);
    }

    [Fact]
    public void ClosureProjection_EscapingCallbackDeclaration_DoesNotFreeGCHandle()
    {
        // The callback declaration should NOT include GCHandle.Free — caller handles it.
        var argProjections = new List<ITypeProjection> { new BlittableProjection("nint") };
        var returnProjection = new BlittableProjection("nint");
        var projection = new ClosureProjection(
            argProjections, returnProjection,
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "callback_doWork_completion");

        var declarations = projection.CallbackDeclarations;
        Assert.Single(declarations);

        var body = declarations[0].Body;
        // Should NOT contain try/finally blocks
        var blockHeaders = body.OfType<MarshalStatement.Block>().Select(b => b.Header).ToList();
        Assert.DoesNotContain("try", blockHeaders);
        Assert.DoesNotContain("finally", blockHeaders);
    }

    #endregion

    #region Invoke Thunk Tests

    [Fact]
    public void EmitSwiftInvokeThunk_IntToInt_UsesTypedMemoryBinding()
    {
        // Verify the Swift thunk uses storeBytes + assumingMemoryBound instead of unsafeBitCast.
        // unsafeBitCast((Int, Int), to: ClosureType) does not handle ARC for the context pointer.
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        ClosureEmitter.EmitSwiftInvokeThunk(
            swiftWriter, closureTypeSpec, closureHandler,
            "SBW_Test_InvCR", "_sbw_inv_test");

        var result = output.ToString();
        // Must use storeBytes + assumingMemoryBound, NOT unsafeBitCast
        Assert.Contains("UnsafeMutableRawPointer.allocate", result);
        Assert.Contains("MemoryLayout<(Int, Int)>.size", result);
        Assert.Contains("storeBytes(of: _funcPtr", result);
        Assert.Contains("storeBytes(of: _context", result);
        Assert.Contains("MemoryLayout<Int>.size", result);
        Assert.Contains("assumingMemoryBound", result);
        Assert.Contains(".pointee", result);
        Assert.DoesNotContain("unsafeBitCast", result);
        Assert.DoesNotContain("Unmanaged<AnyObject>", result);
        // Verify @_cdecl and parameter structure
        Assert.Contains("@_cdecl(\"SBW_Test_InvCR\")", result);
        Assert.Contains("_funcPtr: Int", result);
        Assert.Contains("_context: Int", result);
        Assert.Contains("return _closure(", result);
    }

    [Fact]
    public void EmitSwiftInvokeThunk_VoidReturn_NoReturnStatement()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        ClosureEmitter.EmitSwiftInvokeThunk(
            swiftWriter, closureTypeSpec, closureHandler,
            "SBW_Test_InvCR", "_sbw_inv_test");

        var result = output.ToString();
        Assert.Contains("_closure(arg0)", result);
        Assert.DoesNotContain("return _closure", result);
        // Function signature has no return type (Void closures don't emit "-> Void" in signature)
        Assert.Contains(") {", result);
    }

    [Fact]
    public void EmitSwiftInvokeThunk_BoolArg_EmitsBoolConversion()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Bool) -> Int
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        ClosureEmitter.EmitSwiftInvokeThunk(
            swiftWriter, closureTypeSpec, closureHandler,
            "SBW_Test_InvCR", "_sbw_inv_test");

        var result = output.ToString();
        // Bool args: @_cdecl maps Bool to C _Bool, thunk converts back
        Assert.Contains("arg0 != 0", result);
    }

    [Fact]
    public void EmitSwiftInvokeThunk_BoolReturn_ReturnsBool()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Bool
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        ClosureEmitter.EmitSwiftInvokeThunk(
            swiftWriter, closureTypeSpec, closureHandler,
            "SBW_Test_InvCR", "_sbw_inv_test");

        var result = output.ToString();
        Assert.Contains("-> Bool", result);
    }

    [Fact]
    public void EmitSwiftInvokeThunk_DeallocatesBufferAndUsesMemoryLayout()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        ClosureEmitter.EmitSwiftInvokeThunk(
            swiftWriter, closureTypeSpec, closureHandler,
            "SBW_Test_InvCR", "_sbw_inv_test");

        var result = output.ToString();
        // Buffer must be deallocated via defer
        Assert.Contains("defer { _buf.deallocate() }", result);
        // Must use MemoryLayout for buffer sizing (not hardcoded 16)
        Assert.Contains("MemoryLayout<(Int, Int)>.size", result);
        Assert.Contains("MemoryLayout<(Int, Int)>.alignment", result);
    }

    [Fact]
    public void EmitCSharpInvokeThunkHelper_EmitsDllImportAndInvokerClass()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitCSharpInvokeThunkHelper(
            csWriter, closureTypeSpec, closureHandler,
            "_InvokeClosureThunk_ABCD1234", "SBW_Test_InvCR", "TestLib");

        var result = output.ToString();
        // DllImport P/Invoke
        Assert.Contains("[global::System.Runtime.InteropServices.DllImport(\"TestLib\"", result);
        Assert.Contains("EntryPoint = \"SBW_Test_InvCR\"", result);
        Assert.Contains("CallingConvention.Cdecl", result);
        Assert.Contains("_InvokeClosureThunk_ABCD1234", result);
        Assert.Contains("nint funcPtr", result);
        Assert.Contains("nint ctx", result);
        // Invoker class
        Assert.Contains("_ClosureInv_ABCD1234", result);
        Assert.Contains("private readonly nint _funcPtr", result);
        Assert.Contains("private readonly nint _ctx", result);
        Assert.Contains("Invoke(", result);
    }

    [Fact]
    public void EmitCSharpInvokeThunkHelper_BoolReturn_ByteReturnWithConversion()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Bool
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitCSharpInvokeThunkHelper(
            csWriter, closureTypeSpec, closureHandler,
            "_InvokeClosureThunk_ABCD1234", "SBW_Test_InvCR", "TestLib");

        var result = output.ToString();
        // P/Invoke returns byte (not bool) for Bool
        Assert.Contains("static extern byte _InvokeClosureThunk_ABCD1234", result);
        // Invoker returns bool with conversion
        Assert.Contains("bool Invoke(", result);
        Assert.Contains("!= 0", result);
    }

    [Fact]
    public void CanUseInvokeThunk_PrimitiveArgs_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Int
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        Assert.True(ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, closureHandler));
    }

    [Fact]
    public void CanUseInvokeThunk_VoidReturn_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Int) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, closureHandler));
    }

    [Fact]
    public void CanUseInvokeThunk_ThrowingClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureTypeSpec.Throws = true;

        Assert.False(ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, closureHandler));
    }

    [Fact]
    public void GetInvokeThunkEntryPoint_AppendsSuffix()
    {
        var result = ClosureEmitter.GetInvokeThunkEntryPoint("SBW_TestLib_Free_makeAdder_A6DA40C1");
        Assert.Equal("SBW_TestLib_Free_makeAdder_A6DA40C1_InvCR", result);
    }

    [Fact]
    public void GetInvokeThunkHelperName_IsDeterministic()
    {
        var name1 = ClosureEmitter.GetInvokeThunkHelperName("SBW_Test_InvCR");
        var name2 = ClosureEmitter.GetInvokeThunkHelperName("SBW_Test_InvCR");
        Assert.Equal(name1, name2);
        Assert.StartsWith("_InvokeClosureThunk_", name1);
    }

    [Fact]
    public void GetInvokerClassName_DerivedFromHelperName()
    {
        var className = ClosureEmitter.GetInvokerClassName("_InvokeClosureThunk_ABCD1234");
        Assert.Equal("_ClosureInv_ABCD1234", className);
    }

    [Fact]
    public void CanUseInvokeThunk_ClassReturn_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: () -> Class (TestModule.Loader)
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("TestModule.Loader"));

        Assert.True(ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, closureHandler));
    }

    [Fact]
    public void CanUseInvokeThunk_ObjCBridgedReturn_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: () -> NSError (ObjC-bridged)
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Foundation.NSError"));

        Assert.True(ClosureEmitter.CanUseInvokeThunk(closureTypeSpec, closureHandler));
    }

    [Fact]
    public void IsInvokeThunkCompatibleReturn_ClassType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);
        var classType = new NamedTypeSpec("TestModule.Loader");

        Assert.True(ClosureEmitter.IsInvokeThunkCompatibleReturn(classType, closureHandler));
    }

    [Fact]
    public void IsInvokeThunkCompatibleReturn_ObjCBridgedType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassAndObjC();
        var closureHandler = new ClosureHandler(typeDatabase);
        var objcType = new NamedTypeSpec("Foundation.NSError");

        Assert.True(ClosureEmitter.IsInvokeThunkCompatibleReturn(objcType, closureHandler));
    }

    [Fact]
    public void IsInvokeThunkCompatibleReturn_UnknownType_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var closureHandler = new ClosureHandler(typeDatabase);
        var unknownType = new NamedTypeSpec("SomeModule.SomeStruct");

        Assert.False(ClosureEmitter.IsInvokeThunkCompatibleReturn(unknownType, closureHandler));
    }

    #endregion

    #region String callback marshalling

    [Fact]
    public void EmitIndirectReturnCallback_OptionalStringReturn_EmitsSwiftOptionalSwiftString()
    {
        // Closure: () -> Optional<String>
        // Should emit SwiftOptional<SwiftString> marshalling, not TypeMetadata.GetTypeMetadataOrThrow<string?>()
        var typeDatabase = CreateTypeDatabaseWithString();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String")));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitIndirectReturnCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("SwiftOptional<Swift.SwiftString>", result);
        Assert.Contains("new Swift.SwiftString(result)", result);
        Assert.DoesNotContain("TypeMetadata.GetTypeMetadataOrThrow<string?>", result);
    }

    [Fact]
    public void EmitIndirectReturnCallback_ArrayStringReturn_EmitsSwiftArraySwiftString()
    {
        // Closure: () -> Array<String>
        // Should emit SwiftArray<SwiftString> marshalling, not MarshalToSwift<SwiftArray<string>>
        var typeDatabase = CreateTypeDatabaseWithString();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String")));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitIndirectReturnCallback(
            csWriter, "doWork", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule6doWorkyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("SwiftArray<Swift.SwiftString>", result);
        Assert.Contains("new Swift.SwiftString(_item)", result);
        Assert.Contains("IReadOnlyList<string>", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_StringParam_EmitsMarshalFromSwiftString()
    {
        // Closure: (String) -> Void
        // Callback should marshal via SwiftString.ToString(), not MarshalFromSwift<string>
        var typeDatabase = CreateTypeDatabaseWithString();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.String"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitEscapingClosureCallback(
            csWriter, "logMessage", "handler", closureTypeSpec, closureHandler,
            "$s10TestModule10logMessageyyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("MarshalFromSwift<Swift.SwiftString>", result);
        Assert.Contains(".ToString()", result);
        Assert.DoesNotContain("MarshalFromSwift<string>", result);
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
    {
        var typeDatabase = new TypeDatabase();

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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        return typeDatabase;
    }

    #endregion
}
