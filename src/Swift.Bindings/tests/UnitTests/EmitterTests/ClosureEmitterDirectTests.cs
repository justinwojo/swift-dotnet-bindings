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
    public void SwiftClosureAdapter_ComplexEnumArg_EmitsDeferDeallocate()
    {
        // Complex enum closure args use heap allocation (__heap_N). Each allocation must
        // have a matching defer { __heap_N.deallocate() } to prevent native heap leaks.
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
        Assert.Contains("deinitialize(count: 1); __heap_0.deallocate()", result);
    }

    [Fact]
    public void SwiftClosureAdapter_ComplexEnumArg_WithReturn_EmitsDeferDeallocate()
    {
        // Complex enum arg with a return value: defer ensures deallocation even when
        // the closure body has a return statement.
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
        Assert.Contains("deinitialize(count: 1); __heap_0.deallocate()", result);
        Assert.Contains("return", result);
    }

    [Fact]
    public void SwiftClosureAdapter_MultipleComplexEnumArgs_EmitsDeferForEach()
    {
        // Multiple complex enum args: each __heap_N gets its own defer deallocate.
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
        Assert.Contains("deinitialize(count: 1); __heap_0.deallocate()", result);
        Assert.Contains("deinitialize(count: 1); __heap_1.deallocate()", result);
    }

    [Fact]
    public void SwiftClosureAdapter_ThrowingWithComplexEnumArg_EmitsDeferDeallocate()
    {
        // Throwing closure with complex enum arg: defer ensures cleanup on both
        // success and error paths.
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
        Assert.Contains("deinitialize(count: 1); __heap_0.deallocate()", result);
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
        Assert.Contains("load(as: IndexPath.self)", result);
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
}
