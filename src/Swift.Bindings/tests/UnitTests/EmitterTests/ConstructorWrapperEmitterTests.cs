// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ConstructorWrapperEmitter: per-constructor @_cdecl wrappers that route
/// constructor P/Invokes through C calling convention to avoid CallConvSwift crashes on NativeAOT.
/// </summary>
public class ConstructorWrapperEmitterTests
{
    #region ShouldEmitWrapper Guard Tests

    [Fact]
    public void ShouldEmitWrapper_NonConstructor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("doSomething", isConstructor: false, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_ReturnsTrue()
    {
        // Generic struct parents now supported via protocol-based static factory dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteSignature_ReturnsTrue()
    {
        // Class generic parents with concrete (non-T-referencing) constructor signatures
        // can use @_cdecl wrappers via protocol metatype dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        // Constructor with concrete Int parameter (doesn't reference T)
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGSicfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "capacity",
                    PrivateName = "capacity",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_TReferencingParam_ReturnsTrue()
    {
        // Constructor params that reference the parent's generic type parameter T
        // are now supported via protocol-based static factory dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        // Constructor with T parameter (references generic type param)
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGxcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "value",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("T"),
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void NeedsGenericStaticFactory_GenericStructParent_ReturnsTrue()
    {
        // Generic struct parents always need static factory (no AnyObject for structs)
        var (moduleDecl, typeDb) = CreateTestEnvironment("Wrapper");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Wrapper", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        Assert.True(ConstructorWrapperEmitter.NeedsGenericStaticFactory(
            new MethodEnvironment(method, typeDb), parentDecl));
    }

    [Fact]
    public void NeedsGenericStaticFactory_GenericClassWithTParam_ReturnsTrue()
    {
        // Generic class constructor with T-typed param needs static factory
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericClass", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericClassCyACyxGxcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "value",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("T"),
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        Assert.True(ConstructorWrapperEmitter.NeedsGenericStaticFactory(
            new MethodEnvironment(method, typeDb), parentDecl));
    }

    [Fact]
    public void NeedsGenericStaticFactory_GenericClassConcreteParams_ReturnsFalse()
    {
        // Generic class constructor with concrete (non-T) params uses existing metatype dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        Assert.False(ConstructorWrapperEmitter.NeedsGenericStaticFactory(
            new MethodEnvironment(method, typeDb), parentDecl));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncConstructor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        method.IsAsync = true;

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CdeclCompatibleClosureParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6MyTypeVySiyccfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "handler",
                    PrivateName = "handler",
                    SwiftTypeSpec = closureType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCdeclClosureParam_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);

        // Closure with String arg — not Cdecl-compatible
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6MyTypeVySiyccfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "handler",
                    PrivateName = "handler",
                    SwiftTypeSpec = closureType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialParameter_ReturnsFalse()
    {
        // Direct protocol existential params (any Protocol) cause ABI mismatch:
        // C# P/Invoke passes ExistentialContainer (multi-field struct) by value,
        // but @_cdecl wrapper would need UnsafeRawPointer (single pointer).
        var (moduleDecl, typeDb) = CreateTestEnvironment("Pipeline");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Pipeline", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8PipelineVyAcA11DataCaching_pcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "cache",
                    PrivateName = "cache",
                    SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        // Existential params are now supported in @_cdecl wrappers
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NamedProtocolTypeRecordParameter_ReturnsTrue()
    {
        // Named protocol params (NamedTypeSpec resolving to TypeRecordKind.Protocol) are now
        // supported in @_cdecl wrappers via UnsafeRawPointer + .load(as:) pattern.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("Pipeline",
            ("TestModule.DataCaching", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Pipeline", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8PipelineVyAcA11DataCachingcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "cache",
                    PrivateName = "cache",
                    // NamedTypeSpec (not ProtocolListTypeSpec) — resolves to Protocol via TypeDatabase
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.DataCaching"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ValidConstructor_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClassConstructor_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Animal");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCopyableStruct_ReturnsFalse()
    {
        // ~Copyable types list Escapable WITHOUT Copyable in their conformances.
        // Defense-in-depth guard: skip non-copyable types even though assumingMemoryBound.initialize
        // is ~Copyable-safe, because non-copyable types may have other ABI constraints.
        var (moduleDecl, typeDb) = CreateTestEnvironment("UniqueResource");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("UniqueResource", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            // Only Escapable — no Copyable = ~Copyable type
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.UniqueResource"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule14UniqueResourceVACSWAAMc")
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CopyableStruct_ReturnsTrue()
    {
        // Normal (Copyable) structs don't list Escapable explicitly (pre-Swift 6.2) — should get wrappers.
        var (moduleDecl, typeDb) = CreateTestEnvironment("NormalStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("NormalStruct", moduleDecl);
        // Empty conformances = Copyable (implicit, pre-Swift 6.2)
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_Swift62CopyableStruct_ReturnsTrue()
    {
        // In Swift 6.2+, normal Copyable types explicitly list BOTH Copyable and Escapable.
        // Must still get wrappers — only types with Escapable WITHOUT Copyable are ~Copyable.
        var (moduleDecl, typeDb) = CreateTestEnvironment("NormalStruct62");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("NormalStruct62", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NormalStruct62"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule14NormalStruct62VACSWAAMc"),
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NormalStruct62"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Copyable"),
                "$s10TestModule14NormalStruct62VACsSYAAMc")
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCopyableStructParameter_ReturnsFalse()
    {
        // A copyable type with a non-copyable struct parameter must not get a @_cdecl wrapper.
        // The wrapper passes frozen structs by value, which requires Copyable. C# also passes
        // frozen structs by value, so there's no pointer-based fallback available.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create the non-copyable struct type (with explicit Escapable but NO Copyable conformance)
        var nonCopyableDecl = CreateStructDecl("UniqueToken", moduleDecl);
        nonCopyableDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.UniqueToken"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule11UniqueTokenVACSWAAMc")
        };

        // Create a copyable parent type with a constructor taking the non-copyable param
        var parentDecl = CreateStructDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerVyAcA11UniqueTokenVncfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "token",
                    PrivateName = "token",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.UniqueToken"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CopyableStructParameter_ReturnsTrue()
    {
        // A constructor with only copyable struct parameters should still get a wrapper.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create a normal copyable struct parameter type
        CreateStructDecl("Point", moduleDecl);

        var parentDecl = CreateStructDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerVyAcA5PointVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "point",
                    PrivateName = "point",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CrossModuleNonCopyableStructParameter_ReturnsFalse()
    {
        // A constructor parameter is a non-copyable struct from a DIFFERENT module.
        // FindStructDecl won't find it in ModuleDecl.Types, so the guard must fall back
        // to checking the NonCopyable flag on the TypeRecord in the TypeDatabase.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Register the cross-module non-copyable struct in a separate module database
        var depModule = new ModuleTypeDatabase("DepModule", "/tmp/DepModule.dylib");
        depModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("DepModule.UniqueHandle"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepModule", "UniqueHandle"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("DepModule.UniqueHandle"),
                MetadataAccessor = "$s9DepModule12UniqueHandleVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(depModule);

        // Note: UniqueHandle is NOT in moduleDecl.Types (it's cross-module)
        var parentDecl = CreateStructDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerVyAc9DepModule12UniqueHandleVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "handle",
                    PrivateName = "handle",
                    SwiftTypeSpec = new NamedTypeSpec("DepModule.UniqueHandle"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CrossModuleCopyableStructParameter_ReturnsTrue()
    {
        // A constructor parameter is a normal copyable struct from a different module.
        // The TypeRecord exists but does NOT have the NonCopyable flag — should get a wrapper.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var depModule = new ModuleTypeDatabase("DepModule", "/tmp/DepModule.dylib");
        depModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("DepModule.Config"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("DepModule.Config"),
                MetadataAccessor = "$s9DepModule6ConfigVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(depModule);

        var parentDecl = CreateStructDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerVyAc9DepModule6ConfigVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "config",
                    PrivateName = "config",
                    SwiftTypeSpec = new NamedTypeSpec("DepModule.Config"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_VariadicExpansionPattern_AllUnnamed_ReturnsFalse()
    {
        // init(_:_:_:_:_:) with 4x Disposable + 1x [Disposable] — variadic expansion pattern.
        // Swift expands `init(_ args: Disposable...)` as individual unnamed params + trailing Array.
        // The wrapper can't call this correctly because Swift overload resolution picks the variadic overload.
        var (moduleDecl, typeDb) = CreateTestEnvironment("CompositeDisposable");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("CompositeDisposable", moduleDecl);
        var disposableType = new NamedTypeSpec("TestModule.Disposable");
        var arrayOfDisposable = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Disposable"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule21CompositeDisposableVycfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                // Parser renames "_" labels to "arg0", "arg1", etc. via ExtractParameterNames.
                // The variadic check must handle both "_" and "argN" forms.
                new ArgumentDecl { Name = "arg1", PrivateName = "disposable1", SwiftTypeSpec = disposableType, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { Name = "arg2", PrivateName = "disposable2", SwiftTypeSpec = disposableType, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { Name = "arg3", PrivateName = "disposable3", SwiftTypeSpec = disposableType, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { Name = "arg4", PrivateName = "disposable4", SwiftTypeSpec = disposableType, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { Name = "arg5", PrivateName = "disposables", SwiftTypeSpec = arrayOfDisposable, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_LabeledParamsWithArrayOfSameType_ReturnsTrue()
    {
        // init(primary:all:) with 1x Disposable + 1x [Disposable] — labeled params are genuine overloads,
        // NOT variadic expansions. The wrapper should be emitted normally.
        var (moduleDecl, typeDb) = CreateTestEnvironment("CompositeDisposable");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("CompositeDisposable", moduleDecl);
        var disposableType = new NamedTypeSpec("TestModule.Disposable");
        var arrayOfDisposable = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Disposable"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule21CompositeDisposableVycfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl { Name = "primary", PrivateName = "primary", SwiftTypeSpec = disposableType, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { Name = "all", PrivateName = "all", SwiftTypeSpec = arrayOfDisposable, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayOnlyNoMatchingIndividualParam_ReturnsTrue()
    {
        // init(items:) with just 1x [Disposable] — no individual params of the same element type,
        // so this is NOT a variadic expansion pattern. The wrapper should be emitted normally.
        var (moduleDecl, typeDb) = CreateTestEnvironment("CompositeDisposable");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("CompositeDisposable", moduleDecl);
        var arrayOfDisposable = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Disposable"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule21CompositeDisposableVycfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl { Name = "items", PrivateName = "items", SwiftTypeSpec = arrayOfDisposable, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_HasVariadicParameter_ReturnsFalse()
    {
        // Constructors with variadic params detected from demangler (HasVariadicParameter flag)
        // should skip wrapper emission. This is the definitive variadic check — it catches
        // cases that HasVariadicExpansionPattern doesn't (e.g., single variadic param without
        // matching individual params).
        var (moduleDecl, typeDb) = CreateTestEnvironment("DisposeBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("DisposeBag", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s7RxSwift10DisposeBagCyACypd_tcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "disposables",
                    PrivateName = "disposables",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("RxSwift.Disposable")),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            HasVariadicParameter = true // Set by demangler
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Symbol Naming Tests

    [Fact]
    public void GetConstructorSymbolName_SimpleType()
    {
        var symbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "Nuke", "ImageRequest", "$s4Nuke12ImageRequestVACycfC");
        Assert.StartsWith("SBW_Nuke_ImageRequest_init_", symbol);
    }

    [Fact]
    public void GetConstructorSymbolName_NestedType_DotReplacedWithUnderscore()
    {
        var symbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "Nuke", "ImageRequest.Priority", "$s4Nuke12ImageRequest8PriorityVACycfC");
        Assert.Contains("SBW_Nuke_ImageRequest_Priority_init_", symbol);
        Assert.DoesNotContain(".", symbol);
    }

    [Fact]
    public void GetConstructorSymbolName_DifferentMangledNames_DifferentSymbols()
    {
        var symbol1 = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Counter", "$s10TestModule7CounterVySicfC");
        var symbol2 = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Counter", "$s10TestModule7CounterVySiSicfC");
        Assert.NotEqual(symbol1, symbol2);
    }

    [Fact]
    public void GetConstructorSymbolName_SameMangledName_SameSymbol()
    {
        var mangled = "$s10TestModule7CounterVySicfC";
        var symbol1 = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Counter", mangled);
        var symbol2 = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Counter", mangled);
        Assert.Equal(symbol1, symbol2);
    }

    #endregion

    #region Swift Emission Tests — Struct Constructors

    [Fact]
    public void EmitSwiftWrapper_NonFailableStruct_EmitsCorrectBody()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"", output);
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("resultPtr.assumingMemoryBound(to: TestModule.MyStruct.self).initialize(to: result)", output);
        Assert.DoesNotContain("errorOut", output);
    }

    [Fact]
    public void EmitSwiftWrapper_FailableStruct_EmitsOptionalResult()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: true, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("Optional<TestModule.MyStruct>.self", output);
    }

    [Fact]
    public void EmitSwiftWrapper_ThrowingStruct_EmitsDoTryCatch()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: true);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);
        Assert.Contains("do {", output);
        Assert.Contains("try", output);
        Assert.Contains("} catch {", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftWrapper_FailableThrowingStruct_EmitsOptionalDoTryCatch()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: true, throws: true);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);
        Assert.Contains("Optional<TestModule.MyStruct>.self", output);
        Assert.Contains("errorOut.pointee", output);
    }

    #endregion

    #region Swift Emission Tests — Class Constructors

    [Fact]
    public void EmitSwiftWrapper_NonFailableClass_ReturnsPointer()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: true, isFailable: false, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", output);
        Assert.DoesNotContain("resultPtr", output);
    }

    [Fact]
    public void EmitSwiftWrapper_FailableClass_ReturnsNullablePointer()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: true, isFailable: true, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("guard let result =", output);
        Assert.Contains("return nil", output);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftWrapper_ThrowingClass_EmitsDoTryCatch()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: true, isFailable: false, throws: true);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);
        Assert.Contains("do {", output);
        Assert.Contains("let result = try", output);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
        // Must use bitPattern: 1 (non-nil sentinel), NOT bitPattern: 0 which traps on force-unwrap
        Assert.Contains("UnsafeMutableRawPointer(bitPattern: 1)!", output);
        Assert.DoesNotContain("bitPattern: 0", output);
    }

    [Fact]
    public void EmitSwiftWrapper_FailableThrowingClass_EmitsGuardAndCatch()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: true, isFailable: true, throws: true);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("guard let result = try", output);
        Assert.Contains("return nil", output);
        Assert.Contains("errorOut.pointee", output);
    }

    #endregion

    #region Deduplication Tests

    [Fact]
    public void EmitSwiftWrapper_SecondCallSameSymbol_DoesNotEmit()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false);

        var sw1 = new StringWriter();
        var writer1 = new SwiftWriter(sw1);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer1, env, ctx);
        Assert.NotEmpty(sw1.ToString());

        // Second call with same context — should be deduped
        var sw2 = new StringWriter();
        var writer2 = new SwiftWriter(sw2);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer2, env, ctx);
        Assert.Empty(sw2.ToString());
    }

    [Fact]
    public void EmitSwiftWrapper_DifferentSymbols_EmitsBoth()
    {
        var ctx = new ModuleEmissionContext();

        var (env1, _) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "TypeA", mangledName: "$s10TestModule5TypeAVycfC");
        var (env2, _) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "TypeB", mangledName: "$s10TestModule5TypeBVycfC");

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env1, ctx);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env2, ctx);

        var output = sw.ToString();
        Assert.Contains("TestModule.TypeA", output);
        Assert.Contains("TestModule.TypeB", output);
    }

    #endregion

    #region @MainActor Annotation Tests

    [Fact]
    public void EmitSwiftWrapper_MainActorIsolated_EmitsMainActorAnnotation()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false);
        var parentDecl = env.ParentDecl as TypeDecl;
        parentDecl!.IsMainActorIsolated = true;

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
        Assert.Contains("@_cdecl(\"", output);
    }

    [Fact]
    public void EmitSwiftWrapper_NotMainActorIsolated_NoMainActorAnnotation()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("@MainActor", output);
        Assert.Contains("@_cdecl(\"", output);
    }

    #endregion

    #region Optional String Passthrough Tests (Issue J)

    [Fact]
    public void EmitSwiftWrapper_OptionalStringParam_WithSilgenTarget_PassesPointerThrough()
    {
        // When calling a _dbw_init_* function (silgenTarget != null) with an Optional<String> parameter,
        // the _sbw_init_* wrapper should pass the UnsafeRawPointer through directly instead of
        // loading Optional<String> (which would cause a type mismatch since _dbw_init_* also widens).
        var (moduleDecl, typeDb) = CreateTestEnvironment("Settings");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Settings", moduleDecl);

        // Create Optional<String> type spec
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8SettingsVySSSgcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "licensee",
                    PrivateName = "licensee",
                    SwiftTypeSpec = optionalString,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Settings", method.MangledName);
        method.UsesCdeclConstructorWrapper = true;
        method.MangledName = cdeclSymbol;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        // Pass silgenTarget to simulate calling a _dbw_init_* function (omitLabels=true)
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx, silgenTarget: "_dbw_init_TestSettings");

        var output = sw.ToString();
        // With silgenTarget (calling _dbw_init_*), Optional params that are widened
        // should pass the raw pointer through, NOT load Optional<String>
        Assert.Contains("_ licensee: UnsafeRawPointer", output);
        // Should NOT contain the load pattern for widened Optional params
        Assert.DoesNotContain("licensee.assumingMemoryBound(to: Optional<String>.self).pointee", output);
    }

    [Fact]
    public void EmitSwiftWrapper_OptionalStringParam_WithoutSilgenTarget_LoadsValue()
    {
        // Without silgenTarget (direct init call), Optional<String> should be loaded normally
        var (moduleDecl, typeDb) = CreateTestEnvironment("Settings");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Settings", moduleDecl);

        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8SettingsVySSSgcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "licensee",
                    PrivateName = "licensee",
                    SwiftTypeSpec = optionalString,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Settings", method.MangledName);
        method.UsesCdeclConstructorWrapper = true;
        method.MangledName = cdeclSymbol;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        // Without silgenTarget: direct init call, should load the Optional value
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("licensee.assumingMemoryBound(to: Swift.Optional<Swift.String>.self).pointee", output);
    }

    #endregion

    #region Frozen/Non-Frozen Struct Parameter Tests

    [Fact]
    public void EmitSwiftWrapper_WithFrozenStructParam_PassesAsPointer()
    {
        // Custom frozen structs are now passed as UnsafeRawPointer in @_cdecl wrappers
        // and reconstructed via .load(as: T.self). This avoids "Swift structs cannot be
        // represented in Objective-C" errors at wrapper compilation.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Container",
            ("TestModule.Point", TypeRecordFlags.Frozen, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerCyAcA5PointVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "point",
                    PrivateName = "point",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Container", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Custom frozen struct: passed as UnsafeRawPointer, reconstructed via .load(as:)
        Assert.Contains("_ point: UnsafeRawPointer", output);
        Assert.Contains("point.assumingMemoryBound(to: TestModule.Point.self).pointee", output);
        Assert.Contains("point: pointVal", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithSystemFrozenStructParam_PassesByValue()
    {
        // System framework frozen structs (CoreGraphics.CGPoint, etc.) are C-representable
        // and must be passed by-value in @_cdecl wrappers, NOT via UnsafeRawPointer.
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var cgModule = new ModuleTypeDatabase("CoreGraphics", "/usr/lib/swift/libswiftCoreGraphics.dylib");
        cgModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
                MetadataAccessor = "$sSo7CGPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(cgModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerCyAcSo7CGPointVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "origin",
                    PrivateName = "origin",
                    SwiftTypeSpec = new NamedTypeSpec("CoreGraphics.CGPoint"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Container", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // System frozen struct (CoreGraphics.CGPoint): passed by-value, NOT as UnsafeRawPointer
        Assert.Contains("_ origin: CGPoint", output);
        Assert.DoesNotContain("UnsafeRawPointer", output);
        Assert.Contains("origin: origin", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithNonFrozenStructParam_PassesAsPointer()
    {
        // Non-frozen structs are passed as SafeHandle (IntPtr) from C#,
        // so the @_cdecl wrapper receives them as UnsafeRawPointer and loads.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Container",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerCyAcA6ConfigVcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "config",
                    PrivateName = "config",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Config"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Container", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Non-frozen struct: passed as pointer, needs load
        Assert.Contains("_ config: UnsafeRawPointer", output);
        Assert.Contains("config.assumingMemoryBound(to: TestModule.Config.self).pointee", output);
    }

    #endregion

    #region Enum Raw Value Type Mapping Tests

    [Fact]
    public void EmitSwiftWrapper_WithEnumParam_UnqualifiedRawValueType_EmitsCorrectSwiftType()
    {
        // ABI JSON uses unqualified names like "Int32" not "Swift.Int32".
        // GetSwiftRawValueType must handle both forms.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Widget",
            ("TestModule.Priority", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Int32"));

        var parentDecl = CreateStructDecl("Widget", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6WidgetVyAcA8PriorityOcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "priority",
                    PrivateName = "priority",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Priority"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Widget", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Should emit "Int32" (the correct Swift type), NOT "Int" (the fallback)
        Assert.Contains("_ priority: Int32", output);
        // Use init(rawValue:) for safe conversion — unsafeBitCast crashes when
        // enum storage size differs from parameter type. Guard against invalid raw values.
        Assert.Contains("guard let priorityVal = TestModule.Priority(rawValue: priority) else { preconditionFailure(", output);
        Assert.DoesNotContain("unsafeBitCast", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithSimpleEnumParam_UsesInitRawValue()
    {
        // Issue S: unsafeBitCast crashes when enum storage size differs from parameter
        // type (e.g., a 3-case `: Int` enum stored in 1 byte vs 8-byte Int parameter).
        // Use init(rawValue:) instead, which safely maps raw value → case regardless
        // of in-memory storage size.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Border",
            ("TestModule.Unit", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));

        var parentDecl = CreateStructDecl("Border", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6BorderVyAcA4UnitOcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "unit",
                    PrivateName = "unit",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Unit"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Border", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Must use init(rawValue:) for safe conversion, NOT unsafeBitCast.
        // Guard against invalid raw values from C#.
        Assert.Contains("guard let unitVal = TestModule.Unit(rawValue: unit) else { preconditionFailure(", output);
        Assert.DoesNotContain("unsafeBitCast", output);
        Assert.Contains("_ unit: Int", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithTagOnlyEnumParam_UsesSafeMemoryLoad()
    {
        // Tag-only enums (no RawRepresentable conformance) need safe memory load
        // instead of unsafeBitCast, since enum storage may be smaller than Int.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Widget",
            // null rawValueTypeName = tag-only enum
            ("TestModule.Direction", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, null));

        var parentDecl = CreateStructDecl("Widget", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6WidgetVyAcA9DirectionOcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "direction",
                    PrivateName = "direction",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Direction"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Widget", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Tag-only: uses safe memory load, not unsafeBitCast or init(rawValue:)
        Assert.Contains("_ direction: Int", output); // fallback raw type for null
        Assert.Contains("withUnsafeMutablePointer", output);
        Assert.Contains(".load(as: TestModule.Direction.self)", output);
        Assert.DoesNotContain("unsafeBitCast", output);
        Assert.DoesNotContain("init(rawValue:", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithNSStringTypedefParam_UsesNSStringReconstruction()
    {
        // Issue G: NSString typedef structs (e.g., CALayerContentsGravity) are ObjC-bridged
        // in the type database but are Swift structs, not classes. Unmanaged<T> requires T
        // to be a class, so the wrapper must reconstruct via NSString → String → init(rawValue:).
        var (moduleDecl, typeDb) = CreateTestEnvironment("ContentView");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("ContentView", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule11ContentViewVyACSo27CALayerContentsGravityacfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "contentMode",
                    PrivateName = "contentMode",
                    // QuartzCore.CALayerContentsGravity is in AppleFrameworkRegistry.TypeNameRemaps
                    // mapped to Foundation.NSString
                    SwiftTypeSpec = new NamedTypeSpec("QuartzCore.CALayerContentsGravity"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "ContentView", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Must NOT use Unmanaged<CALayerContentsGravity> (it's a struct, not a class)
        Assert.DoesNotContain("Unmanaged<CALayerContentsGravity>", output);
        // Must reconstruct via NSString → String → init(rawValue:)
        Assert.Contains("Unmanaged<NSString>", output);
        Assert.Contains("as String)", output);
        Assert.Contains("CALayerContentsGravity(rawValue:", output);
    }

    [Theory]
    [InlineData("Swift.Bool", "Bool")]
    [InlineData("Bool", "Bool")]
    [InlineData("Swift.Float", "Float")]
    [InlineData("Float", "Float")]
    [InlineData("Swift.Double", "Double")]
    [InlineData("Double", "Double")]
    [InlineData("CoreFoundation.CGFloat", "CGFloat")]
    [InlineData("CGFloat", "CGFloat")]
    [InlineData("Swift.Int32", "Int32")]
    [InlineData("Int32", "Int32")]
    public void GetSwiftRawValueType_ReturnsCorrectSwiftType(string input, string expected)
    {
        var result = ConstructorWrapperEmitter.GetSwiftRawValueType(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Parameter Mapping Tests

    [Fact]
    public void EmitSwiftWrapper_WithIntParam_PassesThrough()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8MyStructVySicfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "value",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        // Set the mangled name to the cdecl symbol (as MethodHandler would do)
        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "MyStruct", method.MangledName);
        method.MangledName = cdeclSymbol;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ value: Int", output);
        Assert.Contains("value: value", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithBoolParam_EmitsInt8Conversion()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule8MyStructVySbcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "flag",
                    PrivateName = "flag",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "MyStruct", method.MangledName);
        method.MangledName = cdeclSymbol;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ flag: Int8", output);
        Assert.Contains("flagVal = flag != 0", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithSilgenTarget_CallsSilgenFunction()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false);

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
            writer, env, ctx, silgenTarget: "_dbw_init_ABCD1234_1");

        var output = sw.ToString();
        Assert.Contains("_dbw_init_ABCD1234_1", output);
        Assert.Contains("TestModule.MyStruct._dbw_init_ABCD1234_1(", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithSilgenTarget_OmitsArgumentLabels()
    {
        // Issue A: _dbw_init_* functions use _ for all params (no external labels).
        // When silgenTarget is set, argument labels must be omitted.
        var (moduleDecl, typeDb) = CreateTestEnvironment("ImageCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ImageCache", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s4Nuke10ImageCacheCACSi9costLimit_tcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "costLimit",
                    PrivateName = "costLimit",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "ImageCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        // Call with silgenTarget (simulates default-param overload calling _dbw_init_*)
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
            writer, env, ctx, silgenTarget: "_dbw_init_ABCD1234_1");

        var output = sw.ToString();
        // Must NOT contain "costLimit: costLimit" (labeled call to _dbw_init_*)
        Assert.DoesNotContain("costLimit: costLimit", output);
        Assert.DoesNotContain("costLimit: costLimitVal", output);
        // Must call _dbw_init_* with bare args: _dbw_init_ABCD1234_1(costLimit)
        Assert.Contains("_dbw_init_ABCD1234_1(costLimit)", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithoutSilgenTarget_KeepsArgumentLabels()
    {
        // When calling init directly (no silgenTarget), labels must be preserved.
        var (moduleDecl, typeDb) = CreateTestEnvironment("ImageCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ImageCache", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s4Nuke10ImageCacheCACSi9costLimit_tcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "costLimit",
                    PrivateName = "costLimit",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "ImageCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        // Call WITHOUT silgenTarget (direct init call)
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Must contain labeled argument: costLimit: costLimit
        Assert.Contains("costLimit: costLimit", output);
    }

    [Theory]
    [InlineData("in")]
    [InlineData("for")]
    [InlineData("repeat")]
    [InlineData("switch")]
    [InlineData("case")]
    [InlineData("where")]
    [InlineData("return")]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("protocol")]
    [InlineData("func")]
    [InlineData("var")]
    [InlineData("let")]
    public void GetCdeclParamMapping_SwiftKeywordLabel_EscapesWithParamSuffix(string keyword)
    {
        // S1: Swift keywords (in, for, repeat, etc.) used as parameter labels in @_cdecl
        // wrappers cause "keyword cannot be used as an identifier here" compilation errors.
        // Fix: GetCdeclParamMapping renames e.g. "in" -> "inParam".
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Name = keyword,
            PrivateName = keyword,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, _, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, keyword, env);

        // The cdecl param must use the escaped label, not the raw keyword
        Assert.Contains($"{keyword}Param", cdeclParam);
        Assert.DoesNotContain($"_ {keyword}:", cdeclParam);
    }

    [Fact]
    public void GetCdeclParamMapping_NonKeywordLabel_NotEscaped()
    {
        // Normal labels should pass through unchanged
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Name = "count",
            PrivateName = "count",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, _, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, "count", env);

        Assert.Contains("count", cdeclParam);
        Assert.DoesNotContain("countParam", cdeclParam);
    }

    #endregion

    #region Protocol Existential Parameter Tests

    [Fact]
    public void EmitSwiftWrapper_WithProtocolExistentialParam_PassesAsPointer()
    {
        // Issue B: Protocol existentials (any Protocol) are not C-representable.
        // Must marshal as UnsafeRawPointer, not as the protocol type directly.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "AnimationLayer",
            ("TestModule.AnimationProvider", TypeRecordFlags.Frozen, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("AnimationLayer", moduleDecl);

        // Protocol existential as ProtocolListTypeSpec
        var protocolTypeSpec = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.AnimationProvider") });

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule14AnimationLayerCyAcA0C8ProviderpcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "provider",
                    PrivateName = "provider",
                    SwiftTypeSpec = protocolTypeSpec,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "AnimationLayer", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Must be UnsafeRawPointer, NOT the protocol type
        Assert.Contains("_ provider: UnsafeRawPointer", output);
        Assert.DoesNotContain("_ provider: any", output);
        Assert.DoesNotContain("_ provider: AnimationProvider", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithProtocolTypeRecordParam_PassesAsPointer()
    {
        // Protocol types resolved via TypeRecord must also use UnsafeRawPointer.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "Container",
            ("TestModule.MyProtocol", TypeRecordFlags.Frozen, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9ContainerCyAcA10MyProtocolpcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "proto",
                    PrivateName = "proto",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.MyProtocol"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Container", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ proto: UnsafeRawPointer", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithStringParam_UsesTwoIntWords()
    {
        // Issue L: @_cdecl bridges String ↔ NSString* which is incompatible with SwiftString.Buffer.
        // The wrapper must accept two Int words and reconstruct via unsafeBitCast.
        var (moduleDecl, typeDb) = CreateTestEnvironment("ImageRequest");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("ImageRequest", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12ImageRequestVyACSScfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "stringLiteral",
                    PrivateName = "value",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "ImageRequest", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        // Must use two Int words, NOT String type (which @_cdecl bridges to NSString*)
        Assert.Contains("_ _sW0_value: Int", output);
        Assert.Contains("_ _sW1_value: Int", output);
        Assert.DoesNotContain("_ value: String", output);
        // Must reconstruct via unsafeBitCast
        Assert.Contains("unsafeBitCast((_sW0_value, _sW1_value), to: String.self)", output);
        // Must use reconstructed value in call
        Assert.Contains("stringLiteral: valueVal", output);
    }

    [Fact]
    public void EmitSwiftWrapper_WithMultipleStringParams_UsesTwoIntWordsEach()
    {
        // Verify multiple String params each get their own two-word mapping
        var (moduleDecl, typeDb) = CreateTestEnvironment("Pair");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Pair", moduleDecl);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4PairVyACSS_SStcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "first",
                    PrivateName = "first",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    Name = "second",
                    PrivateName = "second",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "Pair", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_sW0_first: Int", output);
        Assert.Contains("_sW1_first: Int", output);
        Assert.Contains("_sW0_second: Int", output);
        Assert.Contains("_sW1_second: Int", output);
        Assert.Contains("unsafeBitCast((_sW0_first, _sW1_first), to: String.self)", output);
        Assert.Contains("unsafeBitCast((_sW0_second, _sW1_second), to: String.self)", output);
    }

    #endregion

    #region Generic Parent Constructor Tests

    [Fact]
    public void EmitSwiftWrapper_GenericClassParent_EmitsProtocolAndMetatypeDispatch()
    {
        // Generic class parent constructors use protocol metatype dispatch:
        // 1. Private protocol with init + AnyObject constraint
        // 2. Retroactive extension conformance
        // 3. Metadata parameter → unsafeBitCast → protocol metatype → init call
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGSicfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "capacity",
                    PrivateName = "capacity",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "GenericCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Protocol: private protocol with AnyObject constraint and init
        Assert.Contains("private protocol _SBW_CI_", output);
        Assert.Contains(": AnyObject", output);
        Assert.Contains("init(capacity: Int)", output);

        // Extension conformance
        Assert.Contains("extension TestModule.GenericCache: _SBW_CI_", output);

        // Metadata parameter
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);

        // Metatype reconstruction via metadata accessor helper
        Assert.Contains("_sbw_meta_", output);
        Assert.Contains("(_metadata0)", output);
        Assert.Contains("unsafeBitCast(parentMeta, to: Any.Type.self)", output);
        Assert.Contains("as! any _SBW_CI_", output);

        // Protocol metatype init call
        Assert.Contains("initType.init(capacity: capacity)", output);

        // Return via Unmanaged with as AnyObject cast
        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftWrapper_GenericClassParent_Failable_EmitsGuardLetPattern()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGSgSicfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = true,
            CSSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "GenericCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Failable protocol: init? in protocol declaration
        Assert.Contains("init?()", output);

        // Failable return type: nullable pointer
        Assert.Contains("-> UnsafeMutableRawPointer?", output);

        // guard let pattern for failable
        Assert.Contains("guard let result = initType.init()", output);
        Assert.Contains("else { return nil }", output);

        // Return via Unmanaged with as AnyObject
        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftWrapper_GenericClassParent_Throwing_EmitsTryCatchPattern()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGSiKcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "GenericCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Throwing protocol: throws in protocol declaration
        Assert.Contains("init() throws", output);

        // Error out parameter
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);

        // try/catch pattern
        Assert.Contains("let result = try initType.init()", output);
        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
        Assert.Contains("} catch {", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);

        // Throwing non-failable returns sentinel pointer on error
        Assert.Contains("UnsafeMutableRawPointer(bitPattern: 1)!", output);
    }

    [Fact]
    public void EmitSwiftWrapper_GenericClassParent_FailableThrowing_EmitsCombinedPattern()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericCache", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule12GenericCacheCyACyxGSgSiKcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = true,
            CSSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "GenericCache", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Failable + throwing protocol
        Assert.Contains("init?() throws", output);

        // Nullable return + error out
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);

        // Combined pattern: try + guard let
        Assert.Contains("guard let result = try initType.init()", output);
        Assert.Contains("else { return nil }", output);
        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);

        // Failable + throwing returns nil on error (not sentinel)
        Assert.Contains("return nil", output);
        Assert.DoesNotContain("bitPattern: 1", output);
    }

    [Fact]
    public void EmitSwiftWrapper_GenericClassParent_MultiGenericParams_AcceptsAllMetadata()
    {
        // Multi-generic parents (e.g., GenericPair<K, V>) accept metadata params for each
        // generic parameter to match PInvokeSignatureBuilder ordering, but only _metadata0
        // (the specialized type metadata) is used for metatype reconstruction.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericPair");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericPair", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("K", "K", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            new("V", "V", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule11GenericPairCyACyxq_GSicfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "capacity",
                    PrivateName = "capacity",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", "GenericPair", method.MangledName);
        method.MangledName = cdeclSymbol;
        method.UsesCdeclConstructorWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Both metadata params accepted in signature
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        Assert.Contains("_ _metadata1: UnsafeRawPointer", output);

        // Both metadata params passed to metadata accessor helper for multi-generic dispatch
        Assert.Contains("_sbw_meta_", output);
        Assert.Contains("(_metadata0, _metadata1)", output);
        Assert.Contains("unsafeBitCast(parentMeta, to: Any.Type.self)", output);
    }

    #endregion

    #region ModuleEmissionContext Tracking Tests

    [Fact]
    public void ModuleEmissionContext_TracksConstructorWrapperSymbols()
    {
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_TestModule_MyStruct_init_ABCD1234";

        Assert.False(ctx.HasConstructorWrapperSymbol(symbol));

        Assert.True(ctx.TryAddConstructorWrapperSymbol(symbol));
        Assert.True(ctx.HasConstructorWrapperSymbol(symbol));

        // Second add returns false (already tracked)
        Assert.False(ctx.TryAddConstructorWrapperSymbol(symbol));
    }

    #endregion

    #region Optional<BlittablePrimitive> Tag Fixup Tests

    [Fact]
    public void EmitSwiftWrapper_StructWithOptionalInt32Property_EmitsTagFixup()
    {
        // A struct with an Optional<Int32> stored property should emit tag byte fixup
        // after initialize(to:) to work around Mono tag corruption.
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "OptConfig");

        // Add Optional<Int32> property to the parent struct
        var parentDecl = env.ParentDecl as TypeDecl;
        var optInt32Spec = new NamedTypeSpec("Swift.Optional");
        optInt32Spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = optInt32Spec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Should contain the standard initialize(to:) call
        Assert.Contains("initialize(to: result)", output);

        // Should contain the tag fixup using MemoryLayout.offset(of:)
        Assert.Contains("MemoryLayout<TestModule.OptConfig>.offset(of: \\TestModule.OptConfig.count)", output);
        Assert.Contains("result.count == nil ? 1 : 0", output);
        // Tag offset for Int32 is 4 bytes
        Assert.Contains("_fo + 4)", output);
    }

    [Fact]
    public void EmitSwiftWrapper_StructWithMultipleOptionalPrimitives_EmitsMultipleFixups()
    {
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "MultiOpt");

        var parentDecl = env.ParentDecl as TypeDecl;

        // Add Optional<Int32> property
        var optInt32Spec = new NamedTypeSpec("Swift.Optional");
        optInt32Spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = optInt32Spec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        // Add Optional<Double> property
        var optDoubleSpec = new NamedTypeSpec("Swift.Optional");
        optDoubleSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "ratio",
            SwiftTypeSpec = optDoubleSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Both properties should have tag fixups
        Assert.Contains("\\TestModule.MultiOpt.count)", output);
        Assert.Contains("result.count == nil ? 1 : 0", output);
        Assert.Contains("_fo + 4)", output);  // Int32 tag offset

        Assert.Contains("\\TestModule.MultiOpt.ratio)", output);
        Assert.Contains("result.ratio == nil ? 1 : 0", output);
        Assert.Contains("_fo + 8)", output);  // Double tag offset
    }

    [Fact]
    public void EmitSwiftWrapper_StructWithoutOptionalBlittable_NoFixup()
    {
        // A struct with only String? and non-optional properties should NOT emit tag fixup
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "NoOptBlittable");

        var parentDecl = env.ParentDecl as TypeDecl;

        // String property (not Optional<BlittablePrimitive>)
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        // Optional<String> property (not blittable primitive)
        var optStringSpec = new NamedTypeSpec("Swift.Optional");
        optStringSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = optStringSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Should NOT contain tag fixup code
        Assert.DoesNotContain("MemoryLayout<", output);
        Assert.DoesNotContain("offset(of:", output);
    }

    [Fact]
    public void EmitSwiftWrapper_ThrowingStructWithOptionalInt32_EmitsTagFixup()
    {
        // Throwing struct constructors should also emit tag fixup
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: true,
            typeName: "ThrowOptConfig");

        var parentDecl = env.ParentDecl as TypeDecl;
        var optInt32Spec = new NamedTypeSpec("Swift.Optional");
        optInt32Spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = optInt32Spec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Should contain do/try/catch structure
        Assert.Contains("do {", output);
        Assert.Contains("try", output);

        // Should contain the tag fixup
        Assert.Contains("MemoryLayout<TestModule.ThrowOptConfig>.offset(of: \\TestModule.ThrowOptConfig.value)", output);
        Assert.Contains("result.value == nil ? 1 : 0", output);
    }

    [Fact]
    public void EmitSwiftWrapper_ClassConstructorWithOptionalInt32_NoFixup()
    {
        // Class constructors return a pointer, not write to resultPtr — no tag fixup needed
        var (env, ctx) = CreateConstructorEnv(isClass: true, isFailable: false, throws: false,
            typeName: "ClassWithOpt");

        var parentDecl = env.ParentDecl as TypeDecl;
        var optInt32Spec = new NamedTypeSpec("Swift.Optional");
        optInt32Spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = optInt32Spec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Class constructors should NOT have tag fixup
        Assert.DoesNotContain("MemoryLayout<", output);
        Assert.DoesNotContain("offset(of:", output);
        // Should return pointer instead
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftWrapper_StructWithComputedOptionalProperty_NoFixup()
    {
        // Computed properties (HasStorage=false) should NOT trigger tag fixup
        var (env, ctx) = CreateConstructorEnv(isClass: false, isFailable: false, throws: false,
            typeName: "ComputedOpt");

        var parentDecl = env.ParentDecl as TypeDecl;
        var optInt32Spec = new NamedTypeSpec("Swift.Optional");
        optInt32Spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        parentDecl!.Properties.Add(new PropertyDecl
        {
            Name = "computed",
            SwiftTypeSpec = optInt32Spec,
            HasStorage = false, // computed, not stored
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = env.MethodDecl.ModuleDecl
        });

        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(writer, env, ctx);

        var output = sw.ToString();

        // Should NOT contain tag fixup for computed properties
        Assert.DoesNotContain("MemoryLayout<", output);
        Assert.DoesNotContain("offset(of:", output);
    }

    [Fact]
    public void GetOptionalBlittablePrimitiveProperties_DetectsAllBlittableTypes()
    {
        // Verify the helper detects various Optional<BlittablePrimitive> types with correct tag offsets
        var moduleDecl = new ModuleDecl
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

        var parentDecl = new StructDecl
        {
            Name = "AllTypes",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AllTypes"),
            MangledName = "$s10TestModule8AllTypesVN",
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
            MetadataAccessor = "$sMa"
        };

        // Add various Optional<BlittablePrimitive> properties
        // Note: Bool is excluded — Optional<Bool> uses extra inhabitants (size == Optional size),
        // not an appended tag byte.
        var types = new (string name, string expectedOffset)[]
        {
            ("Swift.Int8", "1"),
            ("Swift.UInt8", "1"),
            ("Swift.Int16", "2"),
            ("Swift.UInt16", "2"),
            ("Swift.Int32", "4"),
            ("Swift.UInt32", "4"),
            ("Swift.Float", "4"),
            ("Swift.Int64", "8"),
            ("Swift.UInt64", "8"),
            ("Swift.Double", "8"),
            ("Swift.Int", "8"),
        };

        foreach (var (typeName, _) in types)
        {
            var optSpec = new NamedTypeSpec("Swift.Optional");
            optSpec.GenericParameters.Add(new NamedTypeSpec(typeName));
            parentDecl.Properties.Add(new PropertyDecl
            {
                Name = $"opt_{typeName.Replace("Swift.", "").ToLower()}",
                SwiftTypeSpec = optSpec,
                HasStorage = true,
                IsStatic = false,
                Accessors = new List<AccessorDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            });
        }

        // Also add a non-Optional property (should be ignored)
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "plain",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });

        // Also add an Optional<String> (should be ignored — not blittable)
        var optStr = new NamedTypeSpec("Swift.Optional");
        optStr.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "optLabel",
            SwiftTypeSpec = optStr,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });

        var result = ConstructorWrapperEmitter.GetOptionalBlittablePrimitiveProperties(parentDecl);

        // Should detect exactly the blittable types (not plain Int32, not Optional<String>)
        Assert.Equal(types.Length, result.Count);

        for (int i = 0; i < types.Length; i++)
        {
            Assert.Equal(types[i].expectedOffset, result[i].tagOffset);
        }
    }

    #endregion

    #region Helpers

    private static ArgumentDecl CreateReturnArg(ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
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
            MetadataAccessor = "$sMa"
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
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
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static MethodDecl CreateMethod(string name, bool isConstructor, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}VycfC",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        return (moduleDecl, typeDb);
    }

    /// <summary>
    /// Creates a test environment with additional types registered in the TestModule.
    /// Used when constructor parameters need resolvable TypeRecords.
    /// </summary>
    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind, string? rawValueTypeName)[] extraTypes)
    {
        var typeDb = new TypeDatabase();

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        foreach (var (qualifiedName, flags, kind, rawValue) in extraTypes)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
            testModule.RegisterType(
                swiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", swiftTypeName.Name),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                    Flags = flags,
                    Kind = kind,
                    RawValueTypeName = rawValue
                });
        }

        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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

        return (moduleDecl, typeDb);
    }

    /// <summary>
    /// Overload without rawValueTypeName for convenience.
    /// </summary>
    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        return CreateTestEnvironmentWithExtraTypes(
            typeName,
            extraTypes.Select(t => (t.qualifiedName, t.flags, t.kind, (string?)null)).ToArray());
    }

    /// <summary>
    /// Creates a constructor MethodEnvironment for emission tests.
    /// Sets up the MangledName to the cdecl symbol as MethodHandler would.
    /// </summary>
    private static (MethodEnvironment env, ModuleEmissionContext ctx) CreateConstructorEnv(
        bool isClass, bool isFailable, bool throws,
        string typeName = "MyStruct", string mangledName = "")
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment(typeName);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl(typeName, moduleDecl)
            : CreateStructDecl(typeName, moduleDecl);

        if (string.IsNullOrEmpty(mangledName))
            mangledName = $"$s10TestModule{typeName.Length}{typeName}VycfC";

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = mangledName,
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        // Simulate what MethodHandler does: set mangled name to cdecl symbol
        var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
            "TestModule", typeName, method.MangledName);
        method.UsesCdeclConstructorWrapper = true;
        method.MangledName = cdeclSymbol;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        return (env, ctx);
    }

    #endregion

    #region Optional<reference-type> Param Mapping Tests

    [Fact]
    public void GetCdeclParamMapping_OptionalClass_ReturnsNullablePointer()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("Container",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var arg = new ArgumentDecl
        {
            Name = "child",
            PrivateName = "child",
            SwiftTypeSpec = optionalSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(
            arg, "child", env, omitLabels: false);

        // Swift param should be UnsafeMutableRawPointer? (nullable)
        Assert.Contains("UnsafeMutableRawPointer?", cdeclParam);
        // Reconstruction should use Unmanaged.fromOpaque
        Assert.NotNull(reconstruction);
        Assert.Contains("Unmanaged<MyClass>.fromOpaque", reconstruction);
        Assert.Contains("takeUnretainedValue", reconstruction);
    }

    [Fact]
    public void GetCdeclParamMapping_OptionalClass_ReconstructsViaUnmanaged()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("Container",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var arg = new ArgumentDecl
        {
            Name = "child",
            PrivateName = "child",
            SwiftTypeSpec = optionalSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (_, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(
            arg, "child", env, omitLabels: false);

        // Reconstruction uses Optional.map with Unmanaged.fromOpaque
        Assert.NotNull(reconstruction);
        Assert.Contains("child.map {", reconstruction);
        Assert.Contains("MyClass?", reconstruction);
        // Call arg uses reconstructed value
        Assert.Contains("childVal", callArg);
    }

    [Fact]
    public void GetCdeclReturnMapping_OptionalClass_ReturnsOptionalClassPointer()
    {
        var typeDb = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(testModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var (mapping, needsResultPtr) = PropertyWrapperEmitter.GetCdeclReturnMapping(optionalSpec, typeDb);

        Assert.Equal(PropertyWrapperEmitter.CdeclReturnKind.OptionalClassPointer, mapping.Kind);
        Assert.Equal("UnsafeMutableRawPointer?", mapping.cdeclReturnType);
        Assert.False(needsResultPtr);
    }

    [Fact]
    public void GetCdeclParamMapping_OptionalObjCBridgedType_UsesAnyObjectBridge()
    {
        // ObjC-bridged types (e.g., NSZone, IndexPath) get synthetic TypeRecords with
        // Kind=Class and ObjCBridged flag from CreateObjCBridgedTypeRecord. Even though
        // they appear as Class kind, Unmanaged<NSZone> fails at Swift compilation because
        // NSZone is actually a Swift struct. Use Unmanaged<AnyObject> + cast instead.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("Container",
            ("Foundation.NSZone", TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Foundation.NSZone"));

        var arg = new ArgumentDecl
        {
            Name = "zone",
            PrivateName = "zone",
            SwiftTypeSpec = optionalSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(
            arg, "zone", env, omitLabels: false);

        // Swift param should be UnsafeMutableRawPointer? (nullable)
        Assert.Contains("UnsafeMutableRawPointer?", cdeclParam);
        // Reconstruction should use Unmanaged<AnyObject> (NOT Unmanaged<NSZone>)
        Assert.NotNull(reconstruction);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque", reconstruction);
        Assert.Contains("as! NSZone", reconstruction);
        // Should NOT contain Unmanaged<NSZone> — that's the bug this test guards against
        Assert.DoesNotContain("Unmanaged<NSZone>", reconstruction);
    }

    [Fact]
    public void GetCdeclParamMapping_OptionalTrueClass_UsesDirectUnmanaged()
    {
        // True class types (Kind=Class, no ObjCBridged flag) should use Unmanaged<ClassName>
        // directly since they conform to AnyObject.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("Container",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var arg = new ArgumentDecl
        {
            Name = "child",
            PrivateName = "child",
            SwiftTypeSpec = optionalSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (_, reconstruction, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(
            arg, "child", env, omitLabels: false);

        // True class: Unmanaged<MyClass> is safe (no AnyObject bridge needed)
        Assert.NotNull(reconstruction);
        Assert.Contains("Unmanaged<MyClass>.fromOpaque", reconstruction);
        Assert.DoesNotContain("AnyObject", reconstruction);
    }

    #endregion

    #region Issue E: Underscore Parameter Name Tests

    [Fact]
    public void GetCdeclParamMapping_UnderscoreLabel_ProducesUsableIdentifier()
    {
        // Regression test (Issue E): When a Swift method parameter has no external name (uses `_`),
        // the label passed to GetCdeclParamMapping must NOT be `_` because `_` is a discard pattern
        // in Swift and cannot be used as a variable name in reconstruction lines.
        // The calling code should convert `_` to `arg{i}` before calling GetCdeclParamMapping.
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        var parentDecl = CreateStructDecl("TestType", moduleDecl);
        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Name = "_",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        // Simulate the fix: convert `_` to `arg0` before calling GetCdeclParamMapping
        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
        if (label == "_")
            label = "arg0";

        var method = CreateMethod("test", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, label, env, omitLabels: true);

        // The param should use a valid identifier, not `_`
        Assert.Contains("arg0", cdeclParam);
        Assert.DoesNotContain("_ _:", cdeclParam);
        // The call arg should not produce `(: arg0)` — no colon for positional
        Assert.DoesNotContain("(: ", callArg);
    }

    #endregion

    #region @_cdecl Collection Param Marshalling Tests

    /// <summary>
    /// Verifies that GetCdeclParamMapping for generic container types (Array, Dictionary, Set)
    /// produces UnsafeRawPointer param with .load(as:) reconstruction on the Swift side.
    /// The C# side must pass Payload.DangerousGetHandle() (pointer TO the value),
    /// NOT PayloadBuffer.Buffer (which dereferences to the value itself).
    /// Regression test for CryptoSwift HMAC(byte[]) 6.5GB allocation crash.
    /// </summary>
    [Fact]
    public void GetCdeclParamMapping_ArrayContainer_UsesLoadReconstruction()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");

        var containerSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = containerSpec,
            Name = "items",
            PrivateName = "items",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var dummyMethod = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);

        var (cdeclParam, reconstruction, callArg) =
            ConstructorWrapperEmitter.GetCdeclParamMapping(arg, "items", env);

        // Swift side: must use UnsafeRawPointer and .assumingMemoryBound(to:).pointee to read the container value
        Assert.Contains("UnsafeRawPointer", cdeclParam);
        Assert.NotNull(reconstruction);
        Assert.Contains(".assumingMemoryBound(to:", reconstruction);
    }

    [Fact]
    public void GetCdeclParamMapping_DictionaryContainer_UsesLoadReconstruction()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");

        var containerSpec = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.String"));

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = containerSpec,
            Name = "dict",
            PrivateName = "dict",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var dummyMethod = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);

        var (cdeclParam, reconstruction, callArg) =
            ConstructorWrapperEmitter.GetCdeclParamMapping(arg, "dict", env);

        // Swift side: must use UnsafeRawPointer and .assumingMemoryBound(to:).pointee to read the container value
        Assert.Contains("UnsafeRawPointer", cdeclParam);
        Assert.NotNull(reconstruction);
        Assert.Contains(".assumingMemoryBound(to:", reconstruction);
    }

    /// <summary>
    /// Verifies that GetCdeclParamMapping for Foundation.Data accepts two Int words
    /// and reconstructs via unsafeBitCast to Foundation.Data.
    /// Foundation.Data is ObjC-bridged (Data ↔ NSData*) in @_cdecl — passing it by value
    /// causes ABI mismatch (NSData* pointer in GP register vs raw Data buffer bytes).
    /// The two-Int-word pattern avoids ObjC bridging: C# passes Swift.Data (16-byte struct)
    /// in two GP registers, matching two Int parameters on the Swift side.
    /// Regression test for Nuke DataCache.storeData and Lottie LottieAnimation.from SIGSEGV.
    /// </summary>
    [Fact]
    public void GetCdeclParamMapping_FoundationData_UsesTwoIntWords()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");

        var dataSpec = new NamedTypeSpec("Foundation.Data");

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = dataSpec,
            Name = "data",
            PrivateName = "data",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var dummyMethod = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);

        var (cdeclParam, reconstruction, callArg) =
            ConstructorWrapperEmitter.GetCdeclParamMapping(arg, "data", env);

        // Swift @_cdecl must accept two Int words, not Foundation.Data (ObjC bridging)
        Assert.Contains("_dW0_data: Int", cdeclParam);
        Assert.Contains("_dW1_data: Int", cdeclParam);
        Assert.DoesNotContain("Foundation.Data", cdeclParam);

        // Must reconstruct via unsafeBitCast to Foundation.Data.self inside the wrapper body
        Assert.NotNull(reconstruction);
        Assert.Contains("unsafeBitCast", reconstruction);
        Assert.Contains("Foundation.Data.self", reconstruction);

        // Call argument should use the reconstructed value
        Assert.Contains("Val", callArg);
    }

    /// <summary>
    /// Verifies that Foundation.Data parameter labels are preserved correctly
    /// in the @_cdecl wrapper argument expression.
    /// </summary>
    [Fact]
    public void GetCdeclParamMapping_FoundationData_PreservesArgumentLabel()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");

        var dataSpec = new NamedTypeSpec("Foundation.Data");

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = dataSpec,
            Name = "payload",
            PrivateName = "payload",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var dummyMethod = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);

        var (cdeclParam, reconstruction, callArg) =
            ConstructorWrapperEmitter.GetCdeclParamMapping(arg, "payload", env);

        // Argument label should be preserved
        Assert.Equal("payload: payloadVal", callArg);
        Assert.Contains("_dW0_payload", cdeclParam);
        Assert.Contains("_dW1_payload", cdeclParam);
    }

    #endregion
}
