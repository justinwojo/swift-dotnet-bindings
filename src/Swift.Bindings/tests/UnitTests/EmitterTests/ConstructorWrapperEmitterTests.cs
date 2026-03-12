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
    public void ShouldEmitWrapper_GenericParent_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", isConstructor: true, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
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
        Assert.DoesNotContain("licensee.load(as: Optional<String>.self)", output);
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
        Assert.Contains("licensee.load(as: Optional<String>.self)", output);
    }

    #endregion

    #region Frozen/Non-Frozen Struct Parameter Tests

    [Fact]
    public void EmitSwiftWrapper_WithFrozenStructParam_PassesByValue()
    {
        // Frozen structs are passed by value from C# (Buffer or blittable),
        // so the @_cdecl wrapper must accept the Swift type directly.
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
        // Frozen struct: passed by value (Swift type name), NOT as UnsafeRawPointer
        // RenderSwiftTypeSpec may use unqualified name "Point" or qualified "TestModule.Point"
        Assert.Contains("_ point: ", output);
        Assert.Contains("Point", output);
        Assert.DoesNotContain("UnsafeRawPointer", output);
        Assert.Contains("point: point", output);
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
        Assert.Contains("config.load(as: Config.self)", output);
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
        Assert.Contains("guard let priorityVal = Priority(rawValue: priority) else { preconditionFailure(", output);
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
        Assert.Contains("guard let unitVal = Unit(rawValue: unit) else { preconditionFailure(", output);
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
        Assert.Contains(".load(as: Direction.self)", output);
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

    #endregion
}
