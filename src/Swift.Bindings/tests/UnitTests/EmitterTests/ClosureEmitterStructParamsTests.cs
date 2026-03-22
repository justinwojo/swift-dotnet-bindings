// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ClosureEmitter.StructParams — struct parameter marshalling in closure adapters.
/// Covers EmitClosureReturnMarshallingWithStructParams and EmitClosureReturnMarshallingWithNonFrozenParams.
/// </summary>
public class ClosureEmitterStructParamsTests
{
    #region EmitClosureReturnMarshallingWithStructParams

    [Fact]
    public void StructParams_FrozenStruct_EmitsStackallocMarshalling()
    {
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Config) -> Void — frozen struct param
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Config"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("stackalloc byte", result);
        Assert.Contains("SwiftMarshal.MarshalToSwift", result);
        Assert.Contains("SwiftEscapingClosure", result);
        Assert.Contains("_closureWrapper", result);
    }

    [Fact]
    public void StructParams_BoolParam_TreatedAsFrozenStruct()
    {
        // When Bool is registered in the TypeDatabase as a frozen struct,
        // IsFrozenStruct matches it before IsBoolType — Bool gets the
        // stackalloc + MarshalToSwift path (same as any frozen struct).
        // The (byte) conversion branch is a defensive fallback for when
        // Bool is not in the TypeDatabase.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Bool) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Bool"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("stackalloc byte", result);
        Assert.Contains("SwiftMarshal.MarshalToSwift", result);
    }

    [Fact]
    public void StructParams_VoidReturn_NoReturnStatement()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.DoesNotContain("return _fp", result);
        Assert.Contains("_fp(", result);
        Assert.Contains("return _invoker", result);
    }

    [Fact]
    public void StructParams_BoolReturn_EmitsNonZeroCheck()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: () -> Bool
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Swift.Bool"));

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("!= 0", result);
    }

    [Fact]
    public void StructParams_ContextParameter_AddedAsSwiftSelf()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        // SwiftSelf is the context arg added to all function pointer invocations
        Assert.Contains("SwiftSelf", result);
        Assert.Contains("_swiftSelf", result);
    }

    [Fact]
    public void StructParams_SingleParam_NoParensWrapping()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        // Single parameter should use parameterName directly, not (parameterName)
        Assert.Contains("_arg0 =>", result);
    }

    #endregion

    #region EmitClosureReturnMarshallingWithNonFrozenParams

    [Fact]
    public void NonFrozenParams_EmitsNativeMemoryAlloc()
    {
        var typeDatabase = CreateTypeDatabaseWithNonFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Widget) -> Void — non-frozen struct param
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Widget"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("NativeMemory.Alloc", result);
        Assert.Contains("InitializeWithCopy", result);
    }

    [Fact]
    public void NonFrozenParams_EmitsTryFinallyCleanup()
    {
        var typeDatabase = CreateTypeDatabaseWithNonFrozenStruct();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Widget"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("try", result);
        Assert.Contains("finally", result);
        Assert.Contains("Destroy", result);
        Assert.Contains("NativeMemory.Free", result);
    }

    [Fact]
    public void NonFrozenParams_MixedWithFrozen_EmitsBothPatterns()
    {
        var typeDatabase = CreateTypeDatabaseWithBothStructTypes();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Config, Widget) -> Void — Config frozen, Widget non-frozen
        var closureTypeSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new System.Collections.Generic.List<TypeSpec>
            {
                new NamedTypeSpec("TestModule.Config"),
                new NamedTypeSpec("TestModule.Widget")
            }),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        // Frozen struct uses stackalloc
        Assert.Contains("stackalloc byte", result);
        // Non-frozen struct uses NativeMemory
        Assert.Contains("NativeMemory.Alloc", result);
    }

    [Fact]
    public void NonFrozenParams_ClassParam_ExtractsDangerousHandle()
    {
        var typeDatabase = CreateTypeDatabaseWithClass();
        var closureHandler = new ClosureHandler(typeDatabase);
        // Closure: (Loader) -> Void — class param
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Loader"),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(
            csWriter, closureTypeSpec, closureHandler, "result");

        var result = output.ToString();
        Assert.Contains("DangerousGetHandle", result);
    }

    #endregion

    #region Helpers

    private static TypeDatabase CreateTypeDatabase()
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

    private static TypeDatabase CreateTypeDatabaseWithFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithNonFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithBothStructTypes()
    {
        var typeDatabase = CreateTypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithClass()
    {
        var typeDatabase = CreateTypeDatabase();
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
        return typeDatabase;
    }

    #endregion
}
