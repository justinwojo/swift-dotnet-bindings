// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class TypeHandlersOutputTests
{
    [Fact]
    public void Emit_ClassHandler_EmitsClassPayloadAndISwiftObjectSurface()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = new ClassDecl
        {
            Name = "Loader",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            MangledName = "$s10TestModule6LoaderCN",
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

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Contains("public unsafe class Loader : ISwiftObject", csOutput);
        Assert.Contains("SwiftSafeHandle<Loader> _payload", csOutput);
        Assert.Contains("public SwiftSafeHandle<Loader> Payload => _payload;", csOutput);
        Assert.Contains("[DllImport(\"/tmp/TestModule.dylib\", EntryPoint = \"$s10TestModule6LoaderCNMa\")]", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStructHandler_EmitsClassProjectionWithPayload()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("CacheKey", moduleDecl, isFrozen: false, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("public unsafe class CacheKey : ISwiftObject", csOutput);
        Assert.Contains("SwiftSafeHandle<CacheKey> _payload", csOutput);
        Assert.Contains("public SwiftSafeHandle<CacheKey> Payload => _payload;", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_ForValueStruct_EmitsUnsafeStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Point", moduleDecl, isFrozen: true, requiresMemoryManagement: false);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("public unsafe struct Point : ISwiftObject", csOutput);
        Assert.DoesNotContain("public unsafe class Point : ISwiftObject", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_ForReferenceLikeStruct_EmitsClassAndBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Blob", moduleDecl, isFrozen: true, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("public unsafe class Blob : ISwiftObject", csOutput);
        Assert.Contains("public struct Buffer {", csOutput);
        Assert.Contains("public unsafe PayloadBuffer<Blob.Buffer> PayloadBuffer => new PayloadBuffer<Blob.Buffer>(_payload);", csOutput);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        return new TypeDatabase();
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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, bool isFrozen, bool requiresMemoryManagement)
    {
        var structDecl = new StructDecl
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
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);

        return structDecl;
    }

    private static (string csOutput, string swiftOutput) EmitType(TypeDecl typeDecl, TypeDatabase typeDatabase, ITypeHandler handler)
    {
        // Re-register module records per test to keep helpers simple and isolated.
        if (typeDecl is StructDecl structDecl)
        {
            var module = new ModuleTypeDatabase(typeDecl.ModuleDecl!.Name, "/tmp/TestModule.dylib");
            module.RegisterType(
                structDecl.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", structDecl.Name),
                    SwiftTypeName = structDecl.SwiftTypeName,
                    MetadataAccessor = structDecl.MetadataAccessor,
                    Flags = (structDecl.IsFrozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None) |
                            (structDecl.Name == "Blob" || structDecl.Name == "CacheKey" ? TypeRecordFlags.RequiresMemoryManagement : TypeRecordFlags.None),
                    Kind = TypeRecordKind.Struct
                });
            typeDatabase.AddModuleDatabase(module);
        }
        else if (typeDecl is ClassDecl classDecl)
        {
            var module = new ModuleTypeDatabase(typeDecl.ModuleDecl!.Name, "/tmp/TestModule.dylib");
            module.RegisterType(
                classDecl.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", classDecl.Name),
                    SwiftTypeName = classDecl.SwiftTypeName,
                    MetadataAccessor = classDecl.MangledName + "Ma",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                });
            typeDatabase.AddModuleDatabase(module);
        }

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var env = handler.Marshal(typeDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
