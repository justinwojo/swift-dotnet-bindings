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
}
