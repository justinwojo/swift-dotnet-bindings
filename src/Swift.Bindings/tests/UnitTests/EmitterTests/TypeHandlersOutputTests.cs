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

        Assert.Contains("public partial class Loader : ISwiftObject, IDisposable", csOutput);
        Assert.Contains("SwiftClassHandle<Loader> _handle", csOutput);
        Assert.Contains("public SwiftClassHandle<Loader> Payload => _handle;", csOutput);
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\", EntryPoint = \"$s10TestModule6LoaderCNMa\")]", csOutput);
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

        Assert.Contains("public partial class Loader : ISwiftObject, IDisposable, IEquatable<Loader>", csOutput);
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
        Assert.Contains("ISwiftObject, IDisposable, IAnyInterpolatable", csOutput);
        // And the conformance should also be in the dictionary for GetProtocolConformanceDescriptor
        Assert.Contains("{typeof(IAnyInterpolatable)", csOutput);
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

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
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

        Assert.Contains("public partial class Keyframe<T> : ISwiftObject, IDisposable, IEquatable<Keyframe<T>>", csOutput);
        Assert.Contains("var obj = new Keyframe<T>(new SwiftHandle(handle));", csOutput);
        Assert.Contains("Swift.Runtime.SwiftDisposeScope.TryRegister(obj);", csOutput);
        Assert.Contains("return obj;", csOutput);
        Assert.Contains("{typeof(IEquatable<Keyframe<T>>)", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStructHandler_EmitsClassProjectionWithPayload()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("CacheKey", moduleDecl, isFrozen: false, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("public partial class CacheKey : ISwiftObject, ISwiftStruct, IDisposable", csOutput);
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

        Assert.Contains("public unsafe partial struct Point : ISwiftObject, IDisposable", csOutput);
        Assert.DoesNotContain("public unsafe class Point : ISwiftObject, IDisposable", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_ForReferenceLikeStruct_EmitsClassAndBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Blob", moduleDecl, isFrozen: true, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("public partial class Blob : ISwiftObject, ISwiftStruct, IDisposable", csOutput);
        Assert.Contains("public struct Buffer {", csOutput);
        Assert.Contains("public unsafe PayloadBuffer<Blob.Buffer> PayloadBuffer => new PayloadBuffer<Blob.Buffer>(_payload);", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_WithPropertyNestedTypeCollision_RenamesProperty()
    {
        // Parent struct "NetworkConfig" has:
        // - property "configuration" → PascalCase "Configuration"
        // - nested frozen struct "Configuration"
        // Expected: property renamed to "ConfigurationValue" (type keeps original name)
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

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

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.NetworkConfig");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NetworkConfig"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$s10TestModule13NetworkConfigVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var nestedSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.NetworkConfig.Configuration");
        module.RegisterType(nestedSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NetworkConfig.Configuration"),
            SwiftTypeName = nestedSwiftName,
            MetadataAccessor = "$s10TestModule13NetworkConfigV13ConfigurationVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("TestModule");

        var nestedStructDecl = new StructDecl
        {
            Name = "Configuration",
            SwiftTypeName = nestedSwiftName,
            MangledName = "$s10TestModule13NetworkConfigV13ConfigurationVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule13NetworkConfigV13ConfigurationVMa"
        };

        var parentStructDecl = new StructDecl
        {
            Name = "NetworkConfig",
            SwiftTypeName = parentSwiftName,
            MangledName = "$s10TestModule13NetworkConfigVN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "configuration",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStructDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule13NetworkConfigVMa"
        };
        nestedStructDecl.ParentDecl = parentStructDecl;
        moduleDecl.Types.Add(parentStructDecl);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new FrozenStructHandler(new NullLogger<FrozenStructHandler>());
        var env = handler.Marshal(parentStructDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        var output = csOutput.ToString();

        // Nested type should keep original name "Configuration" (no Info suffix)
        Assert.Contains("partial struct Configuration", output);
        Assert.DoesNotContain("ConfigurationInfo", output);
        // TypeDatabase should NOT be modified
        Assert.True(typeDatabase.TryGetTypeRecord(nestedSwiftName, out var updatedRecord));
        Assert.Equal("NetworkConfig.Configuration", updatedRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void Emit_FrozenStructHandler_PropertyTypeIsNestedType_RenamesTypeNotProperty()
    {
        // Property "configuration" → PascalCase "Configuration" collides with nested type "Configuration".
        // Since the property type IS the nested type, rename the nested TYPE (→ "ConfigurationType")
        // instead of the property. This keeps the property name clean for better consumer ergonomics.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("Nuke", "/tmp/Nuke.dylib");

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

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImagePipeline");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Nuke", "ImagePipeline"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$s4Nuke13ImagePipelineVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var nestedSwiftName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImagePipeline.Configuration");
        module.RegisterType(nestedSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Nuke", "ImagePipeline.Configuration"),
            SwiftTypeName = nestedSwiftName,
            MetadataAccessor = "$s4Nuke13ImagePipelineV13ConfigurationVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("Nuke");

        var nestedStructDecl = new StructDecl
        {
            Name = "Configuration",
            SwiftTypeName = nestedSwiftName,
            MangledName = "$s4Nuke13ImagePipelineV13ConfigurationVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s4Nuke13ImagePipelineV13ConfigurationVMa"
        };

        // Property "configuration" with type "Nuke.ImagePipeline.Configuration" (nested type)
        // The InnerType chain: NamedTypeSpec("Nuke.ImagePipeline") -> InnerType -> NamedTypeSpec("Configuration")
        var configTypeSpec = new NamedTypeSpec("Nuke.ImagePipeline")
        {
            InnerType = new NamedTypeSpec("Configuration")
        };

        var parentStructDecl = new StructDecl
        {
            Name = "ImagePipeline",
            SwiftTypeName = parentSwiftName,
            MangledName = "$s4Nuke13ImagePipelineVN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "configuration",
                    SwiftTypeSpec = configTypeSpec,
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStructDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s4Nuke13ImagePipelineVMa"
        };
        nestedStructDecl.ParentDecl = parentStructDecl;
        moduleDecl.Types.Add(parentStructDecl);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        // Pre-pass: apply CSharpTypeName renames before emission
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);

        var handler = new FrozenStructHandler(new NullLogger<FrozenStructHandler>());
        var env = handler.Marshal(parentStructDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        var output = csOutput.ToString();

        // Property should keep original name "Configuration" (no "Value" suffix)
        Assert.DoesNotContain("ConfigurationValue", output);
        // Nested type should be renamed to "ConfigurationType" in C# declaration
        Assert.Contains("partial struct ConfigurationType", output);
    }

    [Fact]
    public void Emit_ClassHandler_NoFinalizer_UsesSwiftClassHandle()
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

        // ARC bridge: no explicit finalizer — SwiftClassHandle's built-in SafeHandle finalizer handles ARC release
        Assert.DoesNotContain("~Loader()", csOutput);
        Assert.Contains("SwiftClassHandle<Loader>", csOutput);
        Assert.Contains("_handle.Dispose()", csOutput);
    }

    [Fact]
    public void Emit_ClassHandler_RenamedNestedClass_SwiftClassHandleUsesRenamedName()
    {
        // Bug: When a nested class is renamed (Animator → AnimatorType to avoid property collision),
        // SwiftClassHandle<T> in the private constructor must use the renamed name.
        // This test exercises the full ClassHandler emission path, not just the helper method.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("Kingfisher", "/tmp/Kingfisher.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("Kingfisher.ImageTransition");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Kingfisher", "ImageTransition"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        var nestedSwiftName = SwiftTypeName.FromModuleQualifiedName("Kingfisher.ImageTransition.Animator");
        module.RegisterType(nestedSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Kingfisher", "ImageTransition.Animator"),
            SwiftTypeName = nestedSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("Kingfisher");

        var nestedClassDecl = new ClassDecl
        {
            Name = "Animator",
            SwiftTypeName = nestedSwiftName,
            MangledName = "$s10Kingfisher15ImageTransitionC8AnimatorCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null!, // Set below
            ModuleDecl = moduleDecl,
        };

        // Property "animator" with type "Kingfisher.ImageTransition.Animator" (nested class)
        var animatorTypeSpec = new NamedTypeSpec("Kingfisher.ImageTransition")
        {
            InnerType = new NamedTypeSpec("Animator")
        };

        var parentClassDecl = new ClassDecl
        {
            Name = "ImageTransition",
            SwiftTypeName = parentSwiftName,
            MangledName = "$s10Kingfisher15ImageTransitionCN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "animator",
                    SwiftTypeSpec = animatorTypeSpec,
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null!, // Set implicitly
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedClassDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        nestedClassDecl.ParentDecl = parentClassDecl;
        moduleDecl.Types.Add(parentClassDecl);

        // Pre-pass: apply CSharpTypeName renames (Animator → AnimatorType)
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);

        // Emit the nested class through ClassHandler
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ClassHandler(new NullLogger<ClassHandler>());
        var env = handler.Marshal(nestedClassDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        var output = csOutput.ToString();

        // The nested class should be renamed to AnimatorType
        Assert.Contains("partial class AnimatorType", output);
        // SwiftClassHandle<T> must use the renamed name everywhere
        Assert.Contains("SwiftClassHandle<AnimatorType>", output);
        // The old unrenamed name must NOT appear in SwiftClassHandle
        Assert.DoesNotContain("SwiftClassHandle<Animator>", output);
    }

    [Fact]
    public void Emit_ClassHandler_Hashable_EmitsSwiftHashableGetHashCode()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = new ClassDecl
        {
            Name = "Point",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MangledName = "$s10TestModule5PointCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    "$s10TestModule5PointCSQAAMc"),
                new(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                    "$s10TestModule5PointCSHAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Contains("SwiftHashable.GetHashCode(this)", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_Equatable_EmitsEquatable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Point", moduleDecl, isFrozen: true, requiresMemoryManagement: false);
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$s10TestModule5PointVSQAAMc"));

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("IEquatable<Point>", csOutput);
        Assert.Contains("SwiftEquatable.Equals", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStructHandler_Equatable_EmitsEquatable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("CacheKey", moduleDecl, isFrozen: false, requiresMemoryManagement: true);
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CacheKey"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$s10TestModule8CacheKeyVSQAAMc"));

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("IEquatable<CacheKey>", csOutput);
        Assert.Contains("SwiftEquatable.Equals", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_ClassProjection_EmitsFinalizer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        // Frozen struct with memory management → class projection with finalizer
        var structDecl = CreateStructDecl("Blob", moduleDecl, isFrozen: true, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("~Blob()", csOutput);
        Assert.Contains("GC.SuppressFinalize", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStructHandler_EmitsFinalizer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("CacheKey", moduleDecl, isFrozen: false, requiresMemoryManagement: true);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("~CacheKey()", csOutput);
        Assert.Contains("GC.SuppressFinalize", csOutput);
    }

    [Fact]
    public void Emit_ClassHandler_Actor_SkipsUnownedExecutor()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Build a getter accessor for the "count" property so it gets fully emitted
        var getterMethod = new MethodDecl
        {
            Name = "count",
            MangledName = "$s10TestModule9DataStoreC5countSivg",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, // set after classDecl created
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var classDecl = new ClassDecl
        {
            Name = "DataStore",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataStore"),
            MangledName = "$s10TestModule9DataStoreCN",
            IsActor = true,
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "unownedExecutor",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.UnownedSerialExecutor"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    Name = "count",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = getterMethod }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        getterMethod.ParentDecl = classDecl;

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        // Actor type should still emit a class declaration
        Assert.Contains("public partial class DataStore", csOutput);
        // Non-runtime property "count" should be emitted (proves actor gate is selective)
        Assert.Contains("Count", csOutput);
        // unownedExecutor should be skipped (actor runtime property)
        Assert.DoesNotContain("UnownedExecutor", csOutput);
        Assert.DoesNotContain("unownedExecutor", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_StoredValueTypeProperty_EmitsTypedField()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftInt();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Counter", moduleDecl, isFrozen: true, requiresMemoryManagement: false);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        Assert.Contains("private long count_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_StoredRefTypeProperty_EmitsCorrectSizedFields()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Person", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // Swift.String is 16 bytes (2 words) — should emit two IntPtr fields
        Assert.Contains("private IntPtr name_0_", csOutput);
        Assert.Contains("private IntPtr name_1_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_StoredRefTypeWithoutInlineSize_EmitsSingleIntPtr()
    {
        // When InlineSize is not set (unknown size), falls back to single IntPtr
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.SomeRefType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SomeRefType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.SomeRefType"),
                MetadataAccessor = "$sSomeRefTypeMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
                // No InlineSize — unknown size, should fall back to IntPtr
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Container", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "data",
            SwiftTypeSpec = new NamedTypeSpec("Swift.SomeRefType"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // Without InlineSize, should emit single IntPtr (backward compatible)
        Assert.Contains("private IntPtr data_", csOutput);
        Assert.DoesNotContain("data_0_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_OptionalStringField_EmitsTwoIntPtrFields()
    {
        // Optional<String> should be 16 bytes (String has extra inhabitants via RequiresMemoryManagement).
        // Previously, the generic Swift.Optional TypeRecord had no InlineSize, causing fallback to 8 bytes.
        var typeDatabase = CreateTypeDatabaseWithOptionalAndString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Config", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String")),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // Optional<String> is 16 bytes (2 words) — should emit two IntPtr fields
        Assert.Contains("private IntPtr label_0_", csOutput);
        Assert.Contains("private IntPtr label_1_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_OptionalInt32Field_EmitsSingleIntPtrField()
    {
        // Optional<Int32> should be 5 bytes (Int32 has no extra inhabitants → size + 1).
        // 5 bytes rounds up to 1 IntPtr (8 bytes).
        var typeDatabase = CreateTypeDatabaseWithOptionalAndInt32();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Settings", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int32")),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // Optional<Int32> is 5 bytes — rounds to 1 IntPtr field
        Assert.Contains("private IntPtr count_", csOutput);
        Assert.DoesNotContain("count_0_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_OptionalStringField_WithoutRequiresMemoryManagement_StillEmitsTwoIntPtrFields()
    {
        // Regression: Swift.Optional can be registered as a frozen enum WITHOUT RequiresMemoryManagement
        // (see ConstructorHandlerOutputTests fixture). The Optional<T> sizing must work regardless of
        // the Optional TypeRecord's flags — the check must happen before the RequiresMemoryManagement gate.
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen, // NO RequiresMemoryManagement — matches ConstructorHandlerOutputTests
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Config", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String")),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // Must still emit two IntPtr fields even without RequiresMemoryManagement on Optional
        Assert.Contains("private IntPtr label_0_", csOutput);
        Assert.Contains("private IntPtr label_1_", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructHandler_MixedOptionalFields_EmitsCorrectLayout()
    {
        // Regression test matching OptionalConfig: Optional<String> + Optional<Int32> + String
        var typeDatabase = CreateTypeDatabaseWithOptionalStringAndInt32();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("OptConfig", moduleDecl, isFrozen: true, requiresMemoryManagement: true);
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String")),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int32")),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });
        structDecl.Properties.Add(new PropertyDecl
        {
            Name = "fallbackLabel",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new FrozenStructHandler(new NullLogger<FrozenStructHandler>()));

        // label: Optional<String> = 16 bytes → 2 IntPtr fields
        Assert.Contains("private IntPtr label_0_", csOutput);
        Assert.Contains("private IntPtr label_1_", csOutput);
        // count: Optional<Int32> = 5 bytes → 1 IntPtr field
        Assert.Contains("private IntPtr count_", csOutput);
        Assert.DoesNotContain("count_0_", csOutput);
        // fallbackLabel: String = 16 bytes → 2 IntPtr fields
        Assert.Contains("private IntPtr fallbackLabel_0_", csOutput);
        Assert.Contains("private IntPtr fallbackLabel_1_", csOutput);
    }

    private static TypeDatabase CreateTypeDatabaseWithOptionalAndString()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
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
                // No InlineSize — generic type, size varies by T
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithOptionalAndInt32()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 4
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
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithOptionalStringAndInt32()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 4
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
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        return new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithSwiftString()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDatabase.AddModuleDatabase(swiftModule);
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", structDecl.Name),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", classDecl.Name),
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
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module.Name, $"I{conformance.Protocol.Name}"),
                    SwiftTypeName = conformance.Protocol,
                    MetadataAccessor = string.Empty,
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
    }
}
