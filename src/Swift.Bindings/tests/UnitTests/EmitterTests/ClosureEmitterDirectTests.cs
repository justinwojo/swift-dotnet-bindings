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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
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
        // Existential return should extract container via ISwiftExistentialConvertible
        Assert.Contains("GetExistentialContainer()", result);
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
}
