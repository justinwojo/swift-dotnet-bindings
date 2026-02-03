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
    public void Emit_ClassHandler_EmitsEquatableConformanceInterface()
    {
        // Only Equatable gets interface emission (has special C# implementation via SwiftEquatable.Equals)
        // Other protocols are tracked in GetProtocolConformanceDescriptor but not as interfaces
        // until protocol method emission on conforming types is implemented.
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
            Conformances = new List<TypeConformance>
            {
                new(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    "$s10TestModule6LoaderCSQAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Contains("public unsafe class Loader : ISwiftObject, IEquatable<Loader>", csOutput);
    }

    [Fact]
    public void Emit_ClassHandler_EmitsSameModuleProtocolConformanceInterfaces()
    {
        // Same-module protocol conformances should be emitted as interfaces
        // so that C# types implement the protocol interface
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
            Conformances = new List<TypeConformance>
            {
                new(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                    SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                    "$s10TestModule6LoaderVAA16AnyInterpolatableAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        // Same-module protocol conformance should appear in the interface list
        Assert.Contains("ISwiftObject, ISwiftAnyInterpolatable", csOutput);
        // And the conformance should also be in the dictionary for GetProtocolConformanceDescriptor
        Assert.Contains("{typeof(ISwiftAnyInterpolatable)", csOutput);
    }

    [Fact]
    public void Emit_ClassHandler_SkipsType_WithUnsupportedSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = new ClassDecl
        {
            Name = "LottieView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LottieView"),
            MangledName = "$s10TestModule10LottieViewCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(
                    "τ_0_0",
                    "T",
                    new List<GenericParameterConformance>
                    {
                        new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"), ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Equal(string.Empty, csOutput.Trim());
    }

    [Fact]
    public void Emit_ClassHandler_GenericClass_UsesTypeArgumentsInSelfReferences()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = new ClassDecl
        {
            Name = "Keyframe",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Keyframe"),
            MangledName = "$s10TestModule8KeyframeCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(
                    "τ_0_0",
                    "T",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>
            {
                new(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.Keyframe"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    "$s10TestModule8KeyframeCSQAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Contains("public unsafe class Keyframe<T0> : ISwiftObject, IEquatable<Keyframe<T0>>", csOutput);
        Assert.Contains("return new Keyframe<T0>(handle);", csOutput);
        Assert.Contains("{typeof(IEquatable<Keyframe<T0>>)", csOutput);
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
            RegisterConformanceProtocols(module, structDecl.Conformances);
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
            RegisterConformanceProtocols(module, classDecl.Conformances);
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

    private static void RegisterConformanceProtocols(ModuleTypeDatabase module, IEnumerable<TypeConformance> conformances)
    {
        var registered = new HashSet<string>();
        foreach (var conformance in conformances.Where(c => c.Protocol.Module == module.Name))
        {
            if (!registered.Add(conformance.Protocol.ModuleQualifiedName))
                continue;

            module.RegisterType(
                conformance.Protocol,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module.Name}", conformance.Protocol.Name),
                    SwiftTypeName = conformance.Protocol,
                    MetadataAccessor = string.Empty,
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
    }
}
