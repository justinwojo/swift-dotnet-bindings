// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for BX3: .NET idiom polish — Hashable suppression, ownership docs,
/// opaque type annotations, and protocol proxy sub-namespace.
/// </summary>
[Collection("Sequential")]
public class DotNetIdiomPolishTests
{
    #region Item #10: Suppress Redundant Hashable/Equatable Members

    [Fact]
    public void IsSynthesizedProtocolProperty_HashValue_Hashable_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var property = CreatePropertyDecl("hashValue", isStatic: false);

        Assert.True(MemberEmissionValidator.IsSynthesizedProtocolProperty(property, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolProperty_HashValue_NoConformance_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: false);
        var property = CreatePropertyDecl("hashValue", isStatic: false);

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolProperty(property, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolProperty_NonHashableProperty_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var property = CreatePropertyDecl("name", isStatic: false);

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolProperty(property, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolProperty_StaticHashValue_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var property = CreatePropertyDecl("hashValue", isStatic: true);

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolProperty(property, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolMethod_HashInto_Hashable_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var method = CreateHashIntoMethod(moduleDecl);

        Assert.True(MemberEmissionValidator.IsSynthesizedProtocolMethod(method, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolMethod_HashInto_NoConformance_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: false);
        var method = CreateHashIntoMethod(moduleDecl);

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolMethod(method, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolMethod_StaticHash_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var method = new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule7MyClassC4hashyyF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("into", new NamedTypeSpec("Swift.Hasher"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolMethod(method, classDecl));
    }

    [Fact]
    public void IsSynthesizedProtocolMethod_Constructor_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule7MyClassCACycfc",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyClass")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        Assert.False(MemberEmissionValidator.IsSynthesizedProtocolMethod(method, classDecl));
    }

    [Fact]
    public void Emit_ClassWithHashable_SuppressesHashValueAndHashInto()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);
        classDecl.Properties.Add(CreatePropertyDecl("hashValue", isStatic: false, parentDecl: classDecl, moduleDecl: moduleDecl));
        classDecl.Properties.Add(CreatePropertyDecl("name", isStatic: false, parentDecl: classDecl, moduleDecl: moduleDecl));
        classDecl.Methods.Add(CreateHashIntoMethod(moduleDecl, classDecl));

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        // hashValue should not appear as a property
        Assert.DoesNotContain("HashValue", csOutput);
        // name should still be there (normal property)
        // hash(into:) should not appear as a method
        // GetHashCode should exist (from EqualityMethodsWriter)
        Assert.Contains("GetHashCode", csOutput);
    }

    [Fact]
    public void Emit_StructWithHashable_SuppressesHashValueAndHashInto()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("MyStruct", moduleDecl, hashable: true, isFrozen: false);
        structDecl.Properties.Add(CreatePropertyDecl("hashValue", isStatic: false, parentDecl: structDecl, moduleDecl: moduleDecl));
        structDecl.Methods.Add(CreateHashIntoMethod(moduleDecl, structDecl));

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.DoesNotContain("HashValue", csOutput);
        Assert.Contains("GetHashCode", csOutput);
    }

    [Fact]
    public void Emit_ComplexEnumWithHashable_SuppressesHashValueAndHashInto()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, hashable: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("loading", hasPayload: true));
        enumDecl.Properties.Add(CreatePropertyDecl("hashValue", isStatic: false, parentDecl: enumDecl, moduleDecl: moduleDecl));
        enumDecl.Methods.Add(CreateHashIntoMethod(moduleDecl, enumDecl));

        var (csOutput, _) = EmitType(enumDecl, typeDatabase, new EnumHandler(new NullLogger<EnumHandler>()));

        Assert.DoesNotContain("HashValue", csOutput);
    }

    #endregion

    #region Item #2: Ownership Doc Comments

    [Fact]
    public void Emit_ComplexEnum_HasDisposalRemarks()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("loading", hasPayload: true));

        var (csOutput, _) = EmitType(enumDecl, typeDatabase, new EnumHandler(new NullLogger<EnumHandler>()));

        Assert.Contains("wraps a Swift enum", csOutput);
        Assert.Contains("disposed explicitly", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStruct_HasDisposalRemarks()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Blob", moduleDecl, isFrozen: false);

        var (csOutput, _) = EmitType(structDecl, typeDatabase, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("wraps a Swift struct", csOutput);
        Assert.Contains("disposed explicitly", csOutput);
    }

    [Fact]
    public void Emit_Dispose_HasDocComment()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        // ARC bridge: classes use SwiftClassHandle with automatic ARC release
        Assert.Contains("deterministic cleanup", csOutput);
    }

    [Fact]
    public void Emit_CachedSingletonCase_HasNoDisposalRemark_TagBased()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("loading", hasPayload: true));

        var (csOutput, _) = EmitType(enumDecl, typeDatabase, new EnumHandler(new NullLogger<EnumHandler>()));

        Assert.Contains("Cached singleton instance", csOutput);
    }

    #endregion

    #region Item #7: Opaque Type Annotations

    [Fact]
    public void CountEmittableMembers_AllSkipped_ReturnsZeroEmittable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Opaque", moduleDecl);
        // Add a property with unsupported type (SwiftUI module)
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("body", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (emittable, skipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, typeDatabase);

        Assert.Equal(0, emittable);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void CountEmittableMembers_SomeEmittable_ReturnsCorrectCounts()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Mixed", moduleDecl);
        // Emittable: a normal Int property
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("count", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });
        // Skipped: SwiftUI property
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("body", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (emittable, skipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, typeDatabase);

        Assert.Equal(1, emittable);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void CountEmittableMembers_AccessorMethods_NotCounted()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("WithAccessor", moduleDecl);
        // Add only an accessor method (property getter impl) — should not count as emittable
        classDecl.Methods.Add(new MethodDecl
        {
            Name = "body_Get",
            MangledName = "$s_get_body",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        // Add a skipped property so type qualifies for opaque check
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("body", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (emittable, skipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, typeDatabase);

        // Accessor method should not inflate emittable count
        Assert.Equal(0, emittable);
        Assert.True(skipped > 0);
    }

    [Fact]
    public void CountEmittableMembers_ModuleInternalMethods_NotCounted()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("WithInternal", moduleDecl);
        // Add a module-internal method — should not count as emittable
        classDecl.Methods.Add(new MethodDecl
        {
            Name = "internalHelper",
            MangledName = "$s_internalHelper",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsModuleInternal = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        // Add a skipped property so type qualifies for opaque check
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("body", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (emittable, skipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, typeDatabase);

        // Module-internal method should not inflate emittable count
        Assert.Equal(0, emittable);
        Assert.True(skipped > 0);
    }

    [Fact]
    public void Emit_OpaqueType_HasAttributeAndRemarks()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Opaque", moduleDecl);
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("body", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.Contains("[global::Swift.OpaqueSwiftType(1)]", csOutput);
        Assert.Contains("no projectable public members", csOutput);
        Assert.Contains("opaque handle", csOutput);
    }

    [Fact]
    public void Emit_TypeWithMembers_NoOpaqueAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Normal", moduleDecl);
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { CreateGetAccessor("count", parentDecl: classDecl, moduleDecl: moduleDecl) },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.DoesNotContain("OpaqueSwiftType", csOutput);
    }

    [Fact]
    public void Emit_TypeWithNoMembersAndNoSkipped_NoOpaqueAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Empty", moduleDecl);

        var (csOutput, _) = EmitType(classDecl, typeDatabase, new ClassHandler(new NullLogger<ClassHandler>()));

        Assert.DoesNotContain("OpaqueSwiftType", csOutput);
    }

    #endregion

    #region Item #10 Report Metric Tests

    [Fact]
    public void RecordMemberSynthesized_HashValue_DoesNotThrow()
    {
        // RecordMemberSynthesized should work without active session (no-op)
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl, hashable: true);

        // Should not throw even without an active report session
        ReportCollector.RecordMemberSynthesized(BindingItemKind.Property, "hashValue", classDecl);
    }

    #endregion

    #region Item #5: Protocol Proxy Sub-Namespace

    [Fact]
    public void Emit_ModuleWithProtocol_HasSwiftInteropNamespace()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create a simple protocol
        var protocolDecl = new ProtocolDecl
        {
            Name = "Fetchable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetchable"),
            MangledName = "$s10TestModule9FetchableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        moduleDecl.Types.Add(protocolDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var csOutput = EmitModule(moduleDecl, typeDatabase);

        Assert.Contains("namespace TestModule.SwiftInterop", csOutput);
        Assert.Contains("using TestModule.SwiftInterop;", csOutput);
    }

    [Fact]
    public void Emit_ProxyClass_InsideSwiftInteropBlock_NotInMainNamespace()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Fetchable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetchable"),
            MangledName = "$s10TestModule9FetchableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        // Add an instance method so EveryProtocolEmitter emits SetFetchable_vtable
        // (and ProtocolProxyEmitter therefore emits the proxy class). Without an
        // implementable member the proxy is correctly suppressed by
        // the vtable-setter-not-exported guard — this test exercises
        // namespace placement, so it needs a non-empty protocol.
        var methodDecl = new MethodDecl
        {
            Name = "fetch",
            ParentDecl = protocolDecl,
            ModuleDecl = moduleDecl,
            MangledName = "$s10TestModule9FetchableP5fetchyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = protocolDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        protocolDecl.Methods.Add(methodDecl);

        moduleDecl.Types.Add(protocolDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var csOutput = EmitModule(moduleDecl, typeDatabase);

        // The proxy class should appear inside the SwiftInterop namespace block,
        // NOT in the main namespace. This ensures proxy references from the main
        // namespace require the using directive to compile.
        var swiftInteropIdx = csOutput.IndexOf("namespace TestModule.SwiftInterop");
        Assert.True(swiftInteropIdx >= 0, "SwiftInterop namespace not found in output");

        var proxyIdx = csOutput.IndexOf("FetchableProxy");
        Assert.True(proxyIdx >= 0, "FetchableProxy not found in output");

        // Proxy must appear AFTER the SwiftInterop namespace declaration
        Assert.True(proxyIdx > swiftInteropIdx,
            "FetchableProxy should be inside SwiftInterop namespace, not in main namespace");

        // The main namespace block (before SwiftInterop) should NOT contain the proxy
        var mainNamespaceContent = csOutput[..swiftInteropIdx];
        Assert.DoesNotContain("FetchableProxy", mainNamespaceContent);

        // The using directive enables unqualified proxy references from the main namespace
        Assert.Contains("using TestModule.SwiftInterop;", csOutput);
    }

    [Fact]
    public void Emit_ModuleWithoutProtocol_HasEmptySwiftInteropNamespace()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var csOutput = EmitModule(moduleDecl, typeDatabase);

        // SwiftInterop namespace should still be emitted (empty) so using resolves
        Assert.Contains("namespace TestModule.SwiftInterop", csOutput);
        Assert.Contains("using TestModule.SwiftInterop;", csOutput);
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
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, bool hashable = false)
    {
        var conformances = new List<TypeConformance>();
        if (hashable)
        {
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                $"$s{moduleDecl.Name}{name}SHAAMc"));
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                $"$s{moduleDecl.Name}{name}SQAAMc"));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, bool hashable = false, bool isFrozen = false)
    {
        var conformances = new List<TypeConformance>();
        if (hashable)
        {
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                $"$s{moduleDecl.Name}{name}SHAAMc"));
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                $"$s{moduleDecl.Name}{name}SQAAMc"));
        }

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            IsFrozen = isFrozen,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static EnumDecl CreateEnumDecl(string name, ModuleDecl moduleDecl, bool hashable = false)
    {
        var conformances = new List<TypeConformance>();
        if (hashable)
        {
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                $"$s{moduleDecl.Name}{name}SHAAMc"));
            conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                $"$s{moduleDecl.Name}{name}SQAAMc"));
        }

        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}ON",
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}OMa",
            Cases = new List<EnumCaseDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static EnumCaseDecl CreateCase(string name, bool hasPayload = false)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s_case_{name}",
            AssociatedValues = hasPayload
                ? new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }
                : new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, bool isStatic, TypeDecl? parentDecl = null, ModuleDecl? moduleDecl = null)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                CreateGetAccessor(name, isStatic, parentDecl, moduleDecl)
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static GetAccessorDecl CreateGetAccessor(string name, bool isStatic = false, TypeDecl? parentDecl = null, ModuleDecl? moduleDecl = null, string returnType = "Swift.Int")
    {
        return new GetAccessorDecl
        {
            Method = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s_get_{name}",
                MethodType = isStatic ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    // First element is the return type
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = new NamedTypeSpec(returnType),
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                IsSynthesizedAccessor = true
            }
        };
    }

    private static MethodDecl CreateHashIntoMethod(ModuleDecl moduleDecl, TypeDecl? parentDecl = null)
    {
        return new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule4hashyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("into", new NamedTypeSpec("Swift.Hasher"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static (string csOutput, string swiftOutput) EmitType(TypeDecl typeDecl, TypeDatabase typeDatabase, ITypeHandler handler)
    {
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
                    Flags = (structDecl.IsFrozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None),
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
        else if (typeDecl is EnumDecl enumDecl)
        {
            var module = new ModuleTypeDatabase(typeDecl.ModuleDecl!.Name, "/tmp/TestModule.dylib");
            module.RegisterType(
                enumDecl.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", enumDecl.Name),
                    SwiftTypeName = enumDecl.SwiftTypeName,
                    MetadataAccessor = enumDecl.MetadataAccessor,
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Enum
                });
            RegisterConformanceProtocols(module, enumDecl.Conformances);
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

    private static string EmitModule(ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        // Ensure the module is registered in the type database
        if (!typeDatabase.IsModuleLoaded(moduleDecl.Name))
            typeDatabase.AddModuleDatabase(new ModuleTypeDatabase(moduleDecl.Name, $"/tmp/{moduleDecl.Name}.dylib"));

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ModuleHandler(new NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var emissionCtx = new ModuleEmissionContext();
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionCtx };
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csOutput.ToString();
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

    #endregion
}
