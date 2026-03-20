// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MetatypeHelperEmitter — shared metadata accessor helper for generic parent types.
/// </summary>
public class MetatypeHelperEmitterTests
{
    private static TypeDecl CreateGenericTypeDecl(string name, string moduleName, string mangledName, int genericParamCount)
    {
        var genericParams = new List<GenericArgumentDecl>();
        for (int i = 0; i < genericParamCount; i++)
        {
            genericParams.Add(new GenericArgumentDecl(
                $"τ_0_{i}",
                i == 0 ? "T" : $"T{i}",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = mangledName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParams,
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    [Fact]
    public void EmitMetadataAccessorHelper_FirstCall_EmitsHelper()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var helperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.StartsWith("_sbw_meta_", helperName);
        Assert.Contains("private func", result);
        Assert.Contains("-> UnsafeRawPointer", result);
        Assert.Contains("dlsym", result);
        Assert.Contains("$s10TestModule12GenericClassCNMa", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_SecondCall_ReturnsSameNameWithoutEmitting()
    {
        var ctx = new ModuleEmissionContext();

        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter1, typeDecl, ctx);

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter2, typeDecl, ctx);

        Assert.Equal(name1, name2);
        Assert.Contains("private func", output1.ToString());
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void EmitMetadataAccessorHelper_SingleGenericParam_OneUnsafeRawPointerParam()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("Box", "TestModule", "$s10TestModule3BoxCN", 1);

        MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.Contains("_ t0: UnsafeRawPointer", result);
        Assert.DoesNotContain("_ t1:", result);
        // Function type should have (Int, UnsafeRawPointer)
        Assert.Contains("(Int, UnsafeRawPointer)", result);
        // Call should be (0, t0)
        Assert.Contains("(0, t0)", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_TwoGenericParams_TwoUnsafeRawPointerParams()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("Pair", "TestModule", "$s10TestModule4PairCN", 2);

        MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.Contains("_ t0: UnsafeRawPointer, _ t1: UnsafeRawPointer", result);
        Assert.Contains("(Int, UnsafeRawPointer, UnsafeRawPointer)", result);
        Assert.Contains("(0, t0, t1)", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_DifferentTypes_DifferentHelperNames()
    {
        var ctx = new ModuleEmissionContext();

        var output1 = new StringWriter();
        var typeDecl1 = CreateGenericTypeDecl("Backend", "DiskStorage", "$s11DiskStorage7BackendCN", 1);
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output1), typeDecl1, ctx);

        var output2 = new StringWriter();
        var typeDecl2 = CreateGenericTypeDecl("Backend", "MemoryStorage", "$s13MemoryStorage7BackendCN", 1);
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output2), typeDecl2, ctx);

        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_HelperNameUsesHash()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var helperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        // Helper name format: _sbw_meta_{8-char hash}
        Assert.Matches(@"^_sbw_meta_[a-fA-F0-9]{8}$", helperName);
    }

    [Fact]
    public void ConstructorForwardingMethod_ProducesSameResult()
    {
        // Verify that the forwarding method in ConstructorWrapperEmitter
        // produces the same result as calling MetatypeHelperEmitter directly
        var ctx1 = new ModuleEmissionContext();
        var ctx2 = new ModuleEmissionContext();
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var output1 = new StringWriter();
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output1), typeDecl, ctx1);

        var output2 = new StringWriter();
        // ConstructorWrapperEmitter forwarding method now requires ITypeDatabase.
        // For this test (no conformances), pwtCount=0, so call MetatypeHelperEmitter directly.
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output2), typeDecl, ctx2, pwtCount: 0);

        Assert.Equal(name1, name2);
        Assert.Equal(output1.ToString(), output2.ToString());
    }
}
