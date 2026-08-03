// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCModuleBuilderTests
{
    [Fact]
    public void Build_SimpleClass_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("MyWidget", configure: c => c
                .Method("doWork", "void")
                .Property("name", "NSString", isPointer: true, isReadonly: true))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("partial interface MyWidget", result);
        Assert.Contains("[Export(\"doWork\")]", result);
        Assert.Contains("string Name", result);
    }

    [Fact]
    public void Build_EnumWithCases_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithEnum("TLStatus", e => e
                .UnderlyingType("NSInteger")
                .Case("TLStatusIdle", 0)
                .Case("TLStatusActive", 1))
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("[Native]", result);
        Assert.Contains("public enum TLStatus : long", result);
        Assert.Contains("Idle = 0", result);
        Assert.Contains("Active = 1", result);
    }

    [Fact]
    public void Build_StructWithFields_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithStruct("TLPoint", ("x", "CGFloat"), ("y", "CGFloat"))
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("public struct TLPoint", result);
        Assert.Contains("public nfloat X;", result);
        Assert.Contains("public nfloat Y;", result);
    }

    [Fact]
    public void Build_Protocol_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol("MyDelegate", p => p
                .Method("didFinish", "void", instance: true))
            .Build();

        var result = EmitApiDefinition(module);
        Assert.Contains("partial interface MyDelegate", result);
        Assert.Contains("[Export(\"didFinish\")]", result);
    }

    [Fact]
    public void Build_Constant_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithConstant("kMaxRetries", "int")
            .Build();

        // Extern constants land in ApiDefinition.cs — it is the only input bgen generates the
        // Dlfcn reader backing a [Field] from.
        var result = EmitApiDefinition(module);
        Assert.Contains("int KMaxRetries", result);
    }

    [Fact]
    public void Build_Function_EmitsCorrectly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction("TLCompute", "double", ("x", "double"), ("y", "double"))
            .Build();

        var result = EmitStructsAndEnums(module);
        Assert.Contains("static extern double TLCompute", result);
    }

    [Fact]
    public void Build_ComplexModule_HasAllDeclarations()
    {
        var module = ObjCModuleBuilder.Create("ComplexLib")
            .WithClass("Widget")
            .WithProtocol("WidgetDelegate")
            .WithEnum("WidgetState", e => e.Case("WidgetStateIdle"))
            .WithStruct("WidgetSize", ("width", "CGFloat"), ("height", "CGFloat"))
            .WithFunction("CreateWidget", "void")
            .WithConstant("kVersion", "int")
            .Build();

        Assert.Equal("ComplexLib", module.ModuleName);
        Assert.Single(module.Classes);
        Assert.Single(module.Protocols);
        Assert.Single(module.Enums);
        Assert.Single(module.Structs);
        Assert.Single(module.Functions);
        Assert.Single(module.Constants);
    }
}
