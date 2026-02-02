// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class OperatorHandlerOutputTests
{
    [Fact]
    public void EmitOperator_BinaryEquality_EmitsWrapperAndPInvoke()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");

        var output = EmitOperator(op, typeDatabase);

        Assert.Contains("operator ==", output);
        Assert.Contains("left", output);
        Assert.Contains("right", output);
        Assert.Contains("[DllImport(\"/tmp/TestModule.dylib\", EntryPoint =", output);
        Assert.Contains("PInvoke_op_Equality", output);
    }

    [Fact]
    public void ValidateAndEmitPairs_WithOnlyEquality_SynthesizesInequality()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        handler.ValidateAndEmitPairs(csWriter, new List<OperatorDecl> { op }, "Point");

        var output = writer.ToString();
        Assert.Contains("public static bool operator !=(Point left, Point right)", output);
        Assert.Contains("return !(left == right);", output);
    }

    [Fact]
    public void EmitOperator_UnsupportedSymbol_EmitsNothing()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("??", parentType, moduleDecl, "Swift.Bool");

        var output = EmitOperator(op, typeDatabase);

        Assert.Equal(string.Empty, output);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
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

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
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
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static OperatorDecl CreateBinaryOperator(string symbol, StructDecl parentType, ModuleDecl moduleDecl, string returnType)
    {
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = "$s10TestModule5PointV2eeoiySbAC_ACtFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec(returnType),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "left",
                    PrivateName = "left",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "right",
                    PrivateName = "right",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = true,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };
    }

    private static string EmitOperator(OperatorDecl op, TypeDatabase typeDatabase)
    {
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());
        handler.EmitOperator(csWriter, op, typeDatabase);
        return writer.ToString();
    }
}
