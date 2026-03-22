// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarkerProtocolOverloadEmitter — Sendable/Copyable overload generation
/// for marker protocols with primitive conformers.
/// </summary>
public class MarkerProtocolOverloadEmitterTests
{
    #region GetPrimitiveConformers

    [Fact]
    public void GetPrimitiveConformers_NullMap_ReturnsEmpty()
    {
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Foo", null);
        Assert.Empty(result);
    }

    [Fact]
    public void GetPrimitiveConformers_MissingProtocol_ReturnsEmpty()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Other"] = new() { "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Foo", map);
        Assert.Empty(result);
    }

    [Fact]
    public void GetPrimitiveConformers_KnownPrimitives_ReturnsMapped()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["ConstraintOffsetTarget"] = new() { "Swift.Double", "Swift.Float", "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("ConstraintOffsetTarget", map);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, c => c.CSharpType == "double" && c.SwiftType == "Swift.Double");
        Assert.Contains(result, c => c.CSharpType == "float" && c.SwiftType == "Swift.Float");
        Assert.Contains(result, c => c.CSharpType == "nint" && c.SwiftType == "Swift.Int");
    }

    [Theory]
    [InlineData("Swift.Double", "double")]
    [InlineData("Swift.Float", "float")]
    [InlineData("Swift.Int", "nint")]
    [InlineData("Swift.UInt", "nuint")]
    [InlineData("CoreFoundation.CGFloat", "nfloat")]
    [InlineData("Swift.Int32", "int")]
    [InlineData("Swift.Int64", "long")]
    [InlineData("Swift.Bool", "bool")]
    public void GetPrimitiveConformers_AllMappedTypes(string swiftType, string expectedCSharp)
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Proto"] = new() { swiftType }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Proto", map);
        Assert.Single(result);
        Assert.Equal(expectedCSharp, result[0].CSharpType);
        Assert.Equal(swiftType, result[0].SwiftType);
    }

    [Fact]
    public void GetPrimitiveConformers_UnknownType_FiltersOut()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Proto"] = new() { "MyModule.CustomStruct", "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Proto", map);
        Assert.Single(result);
        Assert.Equal("nint", result[0].CSharpType);
    }

    #endregion

    #region EmitOverloads gates

    [Fact]
    public void EmitOverloads_NullMap_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doSomething");

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null, null);

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_EmptyMap_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doSomething");

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>>());

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_Constructor_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("init");
        method.IsConstructor = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_AsyncMethod_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doAsync");
        method.IsAsync = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_ThrowingMethod_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doThrow");
        method.Throws = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_GenericParent_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doWork");
        var parentDecl = CreateClassDecl("Container");
        parentDecl.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "Element", new(), new()));

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), parentDecl,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_NoMarkerProtocolParam_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule6doWorkyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("Widget"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_WithMarkerProtocolParam_EmitsOverload()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var swiftOutput = new StringWriter();
        var swWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl("Widget");
        parentDecl.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");

        var markerProtoSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") });
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6Widget6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("amount", markerProtoSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var map = new Dictionary<string, List<string>>
        {
            ["OffsetTarget"] = new() { "Swift.Double", "Swift.Int" }
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swWriter, method,
            CreateMethodEnvironment(method), parentDecl, map);

        var csResult = output.ToString();
        var swiftResult = swiftOutput.ToString();

        // Should emit C# overloads for double and nint
        Assert.Contains("public void Offset(double amount)", csResult);
        Assert.Contains("public void Offset(nint amount)", csResult);
        Assert.Contains("Convenience overload", csResult);

        // Should emit P/Invoke declarations
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("_MP_Double", csResult);
        Assert.Contains("_MP_Int", csResult);

        // Should emit Swift wrappers
        Assert.Contains("@_silgen_name", swiftResult);
        Assert.Contains("Swift.Double", swiftResult);
        Assert.Contains("Swift.Int", swiftResult);
    }

    [Fact]
    public void EmitOverloads_NonVoidNonSelfReturn_Skipped()
    {
        // Methods returning anything other than void or Self are not supported
        var (csWriter, swiftWriter, output) = CreateWriters();
        var moduleDecl = CreateModuleDecl();

        var markerProtoSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") });
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("amount", markerProtoSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("Widget"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var map = new Dictionary<string, List<string>>
        {
            ["OffsetTarget"] = new() { "Swift.Double" }
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null, map);

        Assert.Empty(output.ToString());
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter output) CreateWriters()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter, output);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
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
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
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
    }

    private static MethodDecl CreateBasicMethodDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
