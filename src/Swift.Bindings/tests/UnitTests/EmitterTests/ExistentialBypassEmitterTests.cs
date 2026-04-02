// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class ExistentialBypassEmitterTests
{
    [Fact]
    public void TryEmit_ExistentialParamWithDefaultArg_EmitsBypass()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        // Create existential type argument: "any Equatable"
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // Constructor with bound generic param containing existential, with default
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass should be emitted — both C# factory and Swift wrapper
        Assert.NotEqual(string.Empty, csOutput);
        Assert.NotEqual(string.Empty, swiftOutput);
        Assert.Contains("Create_", csOutput);
        Assert.Contains("SBW_Config_init_", csOutput);
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("SBW_Config_init_", swiftOutput);
        Assert.Contains("SBW_Config_free_", swiftOutput);
    }

    [Fact]
    public void TryEmit_ExistentialParamWithoutDefaultArg_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: false)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass not possible — falls back to skip (empty output)
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ExistentialPlusUnsupportedNonExistentialParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true),
                CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass not possible because non-existential param is not marshallable
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ExistentialPlusPrimitivePassthroughParam_EmitsBypassWithPassthrough()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass emitted with count as passthrough
        Assert.NotEqual(string.Empty, csOutput);
        Assert.NotEqual(string.Empty, swiftOutput);
        Assert.Contains("Create_", csOutput);
        Assert.Contains("count", csOutput);
        Assert.Contains("count", swiftOutput);
    }

    [Fact]
    public void TryEmit_GeneratedSwiftWrapper_UsesMangledHashBasedName()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var mangledHash = ArraySliceNormalizationEmitter.DeterministicHash8(constructor.MangledName);

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains($"SBW_Config_init_{mangledHash}", swiftOutput);
        Assert.Contains($"SBW_Config_free_{mangledHash}", swiftOutput);
        Assert.Contains($"Create_{mangledHash}", csOutput);
    }

    [Fact]
    public void TryEmit_CSharpFactory_UsesTryFinallyCleanup()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("IntPtr swiftPtr = IntPtr.Zero;", csOutput);
        Assert.Contains("try", csOutput);
        Assert.Contains("finally", csOutput);
        Assert.Contains("if (swiftPtr != IntPtr.Zero)", csOutput);
    }

    [Fact]
    public void TryEmit_PInvoke_UsesCallConvCdecl()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("CallConvCdecl", csOutput);
        Assert.Contains("LibraryImport", csOutput);
    }

    [Fact]
    public void TryEmit_PInvoke_UsesCorrectWrapperLibraryPath()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "SwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("SwiftBindings", csOutput);
    }

    [Fact]
    public void TryEmit_BindingReport_RecordsWrappedItem()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        ReportCollector.Start(moduleDecl);
        EmitConstructor(constructor, typeDatabase);
        var report = ReportCollector.Complete();

        Assert.NotNull(report);
        Assert.Single(report.WrappedItems);
        Assert.Equal("ExistentialBypass", report.WrappedItems[0].WrapperKind);
        Assert.NotNull(report.WrappedItems[0].MangledName);

        ReportCollector.Reset();
    }

    // --- Fix validation tests ---

    [Fact]
    public void TryEmit_FailableConstructor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Failable constructors are not supported — bypass should not be attempted
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ThrowingConstructor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            throws: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Throwing constructors are not supported — bypass should not be attempted
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_UnlabeledArgParam_OmitsLabelInSwiftCall()
    {
        // Auto-generated arg names (arg0, arg1) are unlabeled in Swift.
        // Real names like "argIndex" or "arguments" should keep their labels.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgumentWithNames("arg0", "arg0", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // The init call should NOT use "arg0:" label — auto-generated names use bare value.
        Assert.Contains("Config(arg0)", swiftOutput);
    }

    [Fact]
    public void TryEmit_UnderscorePrefixParam_StripsUnderscoreForLabel()
    {
        // Parameters starting with "_" use the stripped name as the label
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgumentWithNames("_value", "_value", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // Should contain "value:" (stripped underscore) as the label
        Assert.Contains("value: _value", swiftOutput);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NamedTypeWithGenericArgs_IncludesGenericParams()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);

        Assert.Equal("Array<Int>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedGenerics_RendersRecursively()
    {
        var inner = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        var outer = new NamedTypeSpec("Swift.Array", inner);

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(outer);

        Assert.Equal("Array<Optional<String>>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_SimpleType_StripsModule()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);

        Assert.Equal("Int", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_EmptyTuple_ReturnsVoid()
    {
        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(TupleTypeSpec.Empty);

        Assert.Equal("Void", result);
    }

    [Fact]
    public void TryEmit_BoundGenericPassthroughNeedingMarshalling_ReturnsFalse()
    {
        // Passthrough param of a bound generic type that needs marshalling (Array<Int> is
        // non-frozen with memory management) should be rejected because the factory can't
        // set up the required marshalling locals.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // Passthrough param: Array<Int> (bound generic but no existential)
        var arrayOfInt = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                // This is a bound generic but NOT existential, so it's a passthrough.
                // However, SwiftArray needs marshalling → wrapper/P/Invoke sigs differ → rejected.
                CreateArgument("items", arrayOfInt, moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass rejected because passthrough arg requires marshalling
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_GenericTypeParameterPassthrough_ReturnsFalse()
    {
        // Passthrough param that is a generic type parameter (IsGeneric=true) should be
        // rejected because the reduced method has no GenericTypeMapping entries.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateGenericArgument("value", new NamedTypeSpec("T"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass rejected because passthrough arg is a generic type parameter
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    // --- Helper methods ---

    private static TypeDatabase CreateTypeDatabase()
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
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

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
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
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);

        // Register the type in the database so marshalling can find it
        var moduleName = moduleDecl.Name;
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
                MetadataAccessor = structDecl.MetadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        return structDecl;
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        bool isFailable = false,
        List<ArgumentDecl>? parameters = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
        {
            signature.AddRange(parameters);
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6ConfigV{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool hasDefault = false)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = hasDefault,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateGenericArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = true,
            HasDefaultArg = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateArgumentWithNames(string name, string privateName, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = privateName,
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EC-7: RenderModuleQualifiedSwiftTypeSpec tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_SimpleType_KeepsModule()
    {
        var typeSpec = new NamedTypeSpec("BonMot.StringStyle");
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
        Assert.Equal("BonMot.StringStyle", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_GenericType_KeepsModuleOnAll()
    {
        var inner = new NamedTypeSpec("Swift.String");
        var outer = new NamedTypeSpec("Swift.Optional", inner);
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(outer);
        Assert.Equal("Swift.Optional<Swift.String>", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_UnqualifiedType_ReturnsAsIs()
    {
        // Types without module prefix (e.g., raw generic params) pass through unchanged
        var typeSpec = new NamedTypeSpec("Int");
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
        Assert.Equal("Int", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_SimpleType_StripsModule_StillWorks()
    {
        // Verify unqualified rendering still works (backward compat)
        var typeSpec = new NamedTypeSpec("BonMot.StringStyle");
        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
        Assert.Equal("StringStyle", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_Tuple_QualifiesElements()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(tuple);
        Assert.Equal("(Swift.Int, Swift.String)", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EC-16: IsAnyObjectType + AnyObject return mapping tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsAnyObjectType_ProtocolList_ReturnsTrue()
    {
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("AnyObject") });
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_QualifiedProtocolList_ReturnsTrue()
    {
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject") });
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_NamedType_ReturnsTrue()
    {
        var anyObject = new NamedTypeSpec("AnyObject");
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_RegularProtocol_ReturnsFalse()
    {
        var proto = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Equatable") });
        Assert.False(CdeclParamMapper.IsAnyObjectType(proto));
    }

    [Fact]
    public void GetCdeclReturnMapping_AnyObject_ReturnsClassPointer()
    {
        var typeDb = new TypeDatabase();
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("AnyObject") });
        var (mapping, needsResultPtr) = CdeclReturnMapping.Classify(anyObject, typeDb);

        Assert.Equal(CdeclReturnKind.ClassPointer, mapping.Kind);
        Assert.False(needsResultPtr);
    }
}
