// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
    public void ShouldEmitWrapper_ClosureParam_ReturnsFalse()
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
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
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
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.MyStruct.self", output);
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
}
