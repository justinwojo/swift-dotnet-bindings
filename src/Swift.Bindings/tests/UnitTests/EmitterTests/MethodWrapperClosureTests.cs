// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Phase 2.5: closure parameters in @_cdecl method/constructor wrappers.
/// Verifies that MethodWrapperEmitter and ConstructorWrapperEmitter handle closure
/// parameters inline when they are Cdecl-compatible, and reject them otherwise.
/// </summary>
public class MethodWrapperClosureTests
{
    #region ShouldEmitWrapper — Method Guards

    [Fact]
    public void ShouldEmitWrapper_CdeclCompatibleClosure_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FrozenStructClosureParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // String (frozen struct) is now Cdecl-compatible via heap allocation in closure adapter
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncThrowingClosure_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Async + throwing closure
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty)
        {
            IsAsync = true,
            Throws = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_PlainAsyncClosure_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Async (non-throwing) closure — adapter is sync-only
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty)
        {
            IsAsync = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MultipleCdeclClosures_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closure1 = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);
        var closure2 = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("Swift.Bool"));

        var method = CreateMethodWithParams("doWork",
            new[] { (closure1 as TypeSpec, "onStart"), (closure2 as TypeSpec, "onComplete") },
            parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MixedCdeclAndNonCdeclClosures_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Register a non-frozen struct for genuinely non-Cdecl-compatible closure arg
        var extraModule = new ModuleTypeDatabase("TestExtra", "/tmp/TestExtra.dylib");
        extraModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestExtra.RuntimeSized"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestExtra", "RuntimeSized"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestExtra.RuntimeSized"),
                MetadataAccessor = "$s9TestExtra11RuntimeSizedVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // NOT frozen
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(extraModule);

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var cdeclClosure = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);
        // Non-frozen struct is genuinely non-Cdecl-compatible
        var nonCdeclClosure = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestExtra.RuntimeSized") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodWithParams("doWork",
            new[] { (cdeclClosure as TypeSpec, "onStart"), (nonCdeclClosure as TypeSpec, "onComplete") },
            parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // NeedsClosureCdeclWrapper requires ALL thunk closures to be Cdecl-compatible
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Emission Tests — Method Wrapper with Closures

    [Fact]
    public void EmitWrapper_ClosureParam_EmitsFuncPtrAndContext()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("callbackFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("callbackContext: UnsafeMutableRawPointer?", output);
        Assert.Contains("_adapted_callback", output);
        Assert.Contains("@convention(c)", output);
    }

    [Fact]
    public void EmitWrapper_OptionalClosure_EmitsNilCheck()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodWithParam("doWork", optionalClosure, "callback", parentDecl, moduleDecl);
        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("callbackFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("var _adapted_callback", output);
        Assert.Contains("nil", output);
        Assert.Contains("if let callbackFuncPtr", output);
    }

    [Fact]
    public void EmitWrapper_ThrowingClosure_EmitsErrorAdapter()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty)
        {
            Throws = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("callbackFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("_adapted_callback", output);
        Assert.Contains("throws", output);
        Assert.Contains("errorPtr", output);
    }

    [Fact]
    public void EmitWrapper_MixedClosureAndPrimitive_EmitsBoth()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodWithParams("doWork",
            new[] { (new NamedTypeSpec("Swift.Int") as TypeSpec, "count"), (closureType as TypeSpec, "callback") },
            parentDecl, moduleDecl);
        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Primitive param
        Assert.Contains("_ count: Int", output);
        // Closure params
        Assert.Contains("callbackFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("callbackContext: UnsafeMutableRawPointer?", output);
        Assert.Contains("_adapted_callback", output);
    }

    #endregion

    #region ShouldEmitWrapper — Constructor Guards

    [Fact]
    public void Constructor_ShouldEmitWrapper_CdeclCompatibleClosure_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);

        var method = CreateConstructorWithParam(closureType, "handler", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void Constructor_ShouldEmitWrapper_FrozenStructClosureParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        // String (frozen struct) is now Cdecl-compatible via heap allocation in closure adapter
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);

        var method = CreateConstructorWithParam(closureType, "handler", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void Constructor_ShouldEmitWrapper_PlainAsyncClosure_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty)
        {
            IsAsync = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateConstructorWithParam(closureType, "handler", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Emission Tests — Constructor Wrapper with Closures

    [Fact]
    public void Constructor_EmitWrapper_ClosureParam_EmitsFuncPtrAndContext()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = CreateEscapingClosure(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);

        var method = CreateConstructorWithParam(closureType, "handler", parentDecl, moduleDecl);
        method.MangledName = "SBW_TestModule_MyType_init_abc12345";
        method.UsesCdeclConstructorWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("handlerFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("handlerContext: UnsafeMutableRawPointer?", output);
        Assert.Contains("_adapted_handler", output);
        Assert.Contains("@convention(c)", output);
    }

    #endregion

    #region HasCdeclClosureMarshalling Tests

    [Fact]
    public void HasCdeclClosureMarshalling_StandaloneWrapper_ReturnsTrue()
    {
        var method = CreateMinimalMethod();
        method.HasClosureCdeclWrapper = true;

        Assert.True(method.HasCdeclClosureMarshalling);
    }

    [Fact]
    public void HasCdeclClosureMarshalling_MethodWrapperWithClosures_ReturnsTrue()
    {
        var method = CreateMinimalMethod();
        method.UsesCdeclMethodWrapper = true;
        method.HasClosureParams = true;

        Assert.True(method.HasCdeclClosureMarshalling);
    }

    [Fact]
    public void HasCdeclClosureMarshalling_ConstructorWrapperWithClosures_ReturnsTrue()
    {
        var method = CreateMinimalMethod();
        method.UsesCdeclConstructorWrapper = true;
        method.HasClosureParams = true;

        Assert.True(method.HasCdeclClosureMarshalling);
    }

    [Fact]
    public void HasCdeclClosureMarshalling_MethodWrapperWithoutClosures_ReturnsFalse()
    {
        var method = CreateMinimalMethod();
        method.UsesCdeclMethodWrapper = true;
        // HasClosureParams is false by default

        Assert.False(method.HasCdeclClosureMarshalling);
    }

    [Fact]
    public void HasCdeclClosureMarshalling_NoWrapper_ReturnsFalse()
    {
        var method = CreateMinimalMethod();

        Assert.False(method.HasCdeclClosureMarshalling);
    }

    #endregion

    #region MethodHandler Integration — Subsumption

    [Fact]
    public void MethodHandler_MethodWrapperSubsumesStandalone()
    {
        // When UsesCdeclMethodWrapper is set on a method with closures,
        // the standalone closure path (HasClosureCdeclWrapper) should NOT also be set.
        // The @_cdecl method wrapper handles closures inline.
        var method = CreateMinimalMethod();
        method.UsesCdeclMethodWrapper = true;
        method.HasClosureParams = true;

        // The method wrapper owns the wrapper, so standalone should be false
        Assert.False(method.HasClosureCdeclWrapper);
        // But Cdecl closure marshalling should be active
        Assert.True(method.HasCdeclClosureMarshalling);
    }

    #endregion

    #region Helper Methods

    private static ClosureTypeSpec CreateEscapingClosure(TypeSpec argsTuple, TypeSpec returnType)
    {
        var closure = new ClosureTypeSpec(argsTuple, returnType);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return closure;
    }

    private static MethodDecl CreateMinimalMethod()
    {
        return new MethodDecl
        {
            Name = "test",
            MangledName = "test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithParam(string name, TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
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
    }

    private static MethodDecl CreateMethodWithParams(string name, (TypeSpec type, string name)[] parameters, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };

        foreach (var (type, paramName) in parameters)
        {
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = type,
                Name = paramName,
                PrivateName = paramName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateConstructorWithParam(TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule_init",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
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

    #endregion
}
