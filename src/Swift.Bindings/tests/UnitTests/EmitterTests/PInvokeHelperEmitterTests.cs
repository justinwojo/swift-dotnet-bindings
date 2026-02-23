#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for PInvokeHelperContext and PInvokeDeclaration.
/// </summary>
public class PInvokeHelperEmitterTests
{
    #region CreateIfGeneric Tests

    [Fact]
    public void CreateIfGeneric_NonGenericType_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Point",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MangledName = "$s10TestModule5PointVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule5PointVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.Null(result);
    }

    [Fact]
    public void CreateIfGeneric_GenericType_ReturnsContext()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$s10TestModule9ContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule9ContainerVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal("Container_PInvoke", result.HelperClassName);
        Assert.Single(result.GenericTypeParameters);
        Assert.Equal("T", result.GenericTypeParameters[0]);
    }

    [Fact]
    public void CreateIfGeneric_TwoTypeParams_HasT0T1()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Pair",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"),
            MangledName = "$s10TestModule4PairVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "A", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
                new("τ_0_1", "B", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule4PairVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal(2, result.GenericTypeParameters.Count);
        Assert.Equal("A", result.GenericTypeParameters[0]);
        Assert.Equal("B", result.GenericTypeParameters[1]);
    }

    #endregion

    #region GetQualifiedTypeName Tests (via HelperClassName)

    [Fact]
    public void GetQualifiedTypeName_SimpleType_ReturnsName()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateGenericStructDecl("Simple", moduleDecl, null);

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal("Simple_PInvoke", result.HelperClassName);
    }

    [Fact]
    public void GetQualifiedTypeName_NestedType_ReturnsParent_Child()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = new StructDecl
        {
            Name = "Outer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
            MangledName = "$s10TestModule5OuterVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule5OuterVMa"
        };

        var childDecl = CreateGenericStructDecl("Inner", moduleDecl, parentDecl);

        var result = PInvokeHelperContext.CreateIfGeneric(childDecl);

        Assert.NotNull(result);
        Assert.Equal("Outer_Inner_PInvoke", result.HelperClassName);
    }

    #endregion

    #region AddDeclaration Tests

    [Fact]
    public void AddDeclaration_Unique_AddsToList()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        };

        context.AddDeclaration(decl);

        Assert.Single(context.Declarations);
    }

    [Fact]
    public void AddDeclaration_DuplicateMethodName_Deduplicates()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        var decl1 = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest1",
            MethodName = "PInvoke_getMetadata",
            ReturnType = "TypeMetadata",
            ParametersString = "",
            IsAsync = false
        };
        var decl2 = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest2",
            MethodName = "PInvoke_getMetadata",
            ReturnType = "TypeMetadata",
            ParametersString = "",
            IsAsync = false
        };

        context.AddDeclaration(decl1);
        context.AddDeclaration(decl2);

        Assert.Single(context.Declarations);
    }

    #endregion

    #region EmitHelperClass Tests

    [Fact]
    public void EmitHelperClass_EmitsPartialClass()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        context.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        });

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        context.EmitHelperClass(csWriter);

        var result = output.ToString();
        Assert.Contains("internal static partial class MyType_PInvoke", result);
    }

    [Fact]
    public void EmitHelperClass_EmitsLibraryImportDeclarations()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        context.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTestEntryPoint",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        });

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        context.EmitHelperClass(csWriter);

        var result = output.ToString();
        Assert.Contains("[LibraryImport(\"/tmp/lib.dylib\", EntryPoint = \"$sTestEntryPoint\")]", result);
        Assert.Contains("internal static partial void PInvoke_doWork(IntPtr self);", result);
    }

    #endregion

    #region PInvokeDeclaration.Emit Tests

    [Fact]
    public void PInvokeDeclaration_Emit_BoolReturn_AddsMarshalAs()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_isValid",
            ReturnType = "bool",
            ParametersString = "",
            IsAsync = false
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("[return: MarshalAs(UnmanagedType.U1)]", result);
        Assert.Contains("internal static partial bool PInvoke_isValid();", result);
    }

    [Fact]
    public void PInvokeDeclaration_Emit_AsyncMethod_ReturnsVoid()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_load",
            ReturnType = "Int64",
            ParametersString = "void* callback",
            IsAsync = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("internal static partial void PInvoke_load(void* callback);", result);
        Assert.DoesNotContain("Int64", result);
    }

    [Fact]
    public void PInvokeDeclaration_Emit_WithMetadataParams_AppendsToSignature()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false,
            MetadataParameters = new[] { "TypeMetadata t0Metadata" }
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("PInvoke_doWork(IntPtr self, TypeMetadata t0Metadata);", result);
    }

    #endregion

    #region Helper Methods

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

    private static StructDecl CreateGenericStructDecl(string name, ModuleDecl moduleDecl, TypeDecl? parentDecl)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = (BaseDecl?)parentDecl ?? moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    #endregion
}
