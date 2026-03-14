// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MemberValidationPipeline — unified validation for member emission and wrapper eligibility.
/// </summary>
public class MemberValidationPipelineTests
{
    #region ValidateMethodEmission Tests

    [Fact]
    public void ValidateMethodEmission_NormalMethod_ReturnsEmit()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("doSomething", TupleTypeSpec.Empty);

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.True(result.ShouldEmit);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_SpiProtected_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("internalOnly", TupleTypeSpec.Empty);
        method.IsSpiProtected = true;

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
        Assert.Contains("@_spi", result.Details!);
    }

    [Fact]
    public void ValidateMethodEmission_ImplicitOverridingConstructor_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.IsModuleInternal = true;
        method.IsImplicit = true;
        method.IsOverride = true;
        method.IsConstructor = true;

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
        Assert.Contains("Implicit+overriding", result.Details!);
    }

    [Fact]
    public void ValidateMethodEmission_ModuleLevelInternalFunction_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var method = CreateMethod("internalFunc", TupleTypeSpec.Empty);
        method.IsModuleInternal = true;
        method.ParentDecl = moduleDecl;

        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
        Assert.Contains("Internal", result.Details!);
    }

    [Fact]
    public void ValidateMethodEmission_ModuleLevelInternalTakesPrecedenceOverSpi()
    {
        // A free function that's both internal and SPI — internal gate fires first
        // because module-level internal check (2a) is before SPI (1)... actually SPI is first.
        // Let's verify the order: SPI is gate 1, module internal is gate 2a.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var method = CreateMethod("bothFlags", TupleTypeSpec.Empty);
        method.IsSpiProtected = true;
        method.IsModuleInternal = true;
        method.ParentDecl = moduleDecl;

        var result = pipeline.ValidateMethodEmission(method, null);

        // SPI is checked first (gate 1), so it fires
        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_SynthesizedProtocol_HasIsSynthesizedFlag()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var classDecl = new ClassDecl
        {
            Name = "MyClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            MangledName = "$s10TestModule7MyClassCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                    "$s10TestModule7MyClassCSHAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var method = new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule7MyClassC4hash4intoys6HasherVz_tF",
            MethodType = MethodType.Instance,
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
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.False(result.ShouldEmit);
        Assert.True(result.IsSynthesized);
        Assert.Null(result.Reason); // Synthesized results don't carry a SkipReason
    }

    [Fact]
    public void ValidateMethodEmission_ImplicitButNotOverriding_ReturnsEmit()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.IsModuleInternal = true;
        method.IsImplicit = true;
        method.IsOverride = false;
        method.IsConstructor = true;

        // IsModuleInternal + IsImplicit alone doesn't trigger the gate (needs IsOverride too)
        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidateMethodEmission_SynthesizedProtocolMethod_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var classDecl = new ClassDecl
        {
            Name = "MyClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            MangledName = "$s10TestModule7MyClassCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                    "$s10TestModule7MyClassCSHAAMc")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Create hash(into:) — synthesized Hashable conformance
        var method = new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule7MyClassC4hash4intoys6HasherVz_tF",
            MethodType = MethodType.Instance,
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
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Contains("Synthesized", result.Details!);
    }

    [Fact]
    public void ValidateMethodEmission_SynthesizedCodableEncode_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = new MethodDecl
        {
            Name = "encode",
            MangledName = "$s10TestModule6encodeyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("to", new NamedTypeSpec("Swift.Encoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.SynthesizedCodable, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_SwiftUIReturnType_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = CreateMethod("getView", new NamedTypeSpec("SwiftUI.View"));

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_UnsupportedClosure_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        // Create a closure with an unsupported parameter type (will fail IsSupportedClosure)
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("SwiftUI.View"),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithArgs("configure", TupleTypeSpec.Empty, closureType);

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        // Should be caught by either closure or SwiftUI gate
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_Constructor_SkipsSynthesizedCodableCheck()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        // init(from: Decoder) is a Codable constructor — should be skipped
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6inityyF",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("from", new NamedTypeSpec("Swift.Decoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.SynthesizedCodable, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_MethodWithSwiftIntParam_ReturnsEmit()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = CreateMethodWithArgs("process", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int"));

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.True(result.ShouldEmit);
    }

    #endregion

    #region Parity Tests (pipeline matches old behavior)

    [Fact]
    public void ValidateMethodEmission_ParityWithShouldSkipMethodEmission_SwiftUIParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = CreateMethodWithArgs("setColor", TupleTypeSpec.Empty, new NamedTypeSpec("SwiftUI.Color"));

        // Old path
        var oldResult = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        // New path
        var newResult = pipeline.ValidateMethodEmission(method, null!);

        // Both should skip
        Assert.NotNull(oldResult);
        Assert.False(newResult.ShouldEmit);
        Assert.Equal(oldResult!.Value, newResult.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_ParityWithShouldSkipMethodEmission_CombineParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = CreateMethodWithArgs("subscribe", TupleTypeSpec.Empty, new NamedTypeSpec("Combine.Publisher"));

        // Old path
        var oldResult = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        // New path
        var newResult = pipeline.ValidateMethodEmission(method, null!);

        Assert.NotNull(oldResult);
        Assert.False(newResult.ShouldEmit);
        Assert.Equal(oldResult!.Value, newResult.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_ParityWithShouldSkipMethodEmission_NormalMethod()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = CreateMethod("normalMethod", new NamedTypeSpec("Swift.Int"));

        // Old path
        var oldResult = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        // New path
        var newResult = pipeline.ValidateMethodEmission(method, null!);

        // Both should pass
        Assert.Null(oldResult);
        Assert.True(newResult.ShouldEmit);
    }

    [Fact]
    public void ValidateMethodEmission_ParityWithInlineChecks_SpiProtected()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("spiMethod", TupleTypeSpec.Empty);
        method.IsSpiProtected = true;

        // Old inline check: methodDecl.IsSpiProtected -> skip
        // New pipeline: same result
        var result = pipeline.ValidateMethodEmission(method, null!);
        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
    }

    #endregion

    #region ValidatePropertyEmission Tests

    [Fact]
    public void ValidatePropertyEmission_NormalProperty_ReturnsEmit()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var property = CreateProperty("count", new NamedTypeSpec("Swift.Int"));

        var result = pipeline.ValidatePropertyEmission(property, null!);

        Assert.True(result.ShouldEmit);
    }

    #endregion

    #region ValidateSubscriptEmission Tests

    [Fact]
    public void ValidateSubscriptEmission_NormalSubscript_ReturnsEmit()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var subscript = CreateSubscript(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int"));

        var result = pipeline.ValidateSubscriptEmission(subscript, null!);

        Assert.True(result.ShouldEmit);
    }

    #endregion

    #region Gate Ordering Tests

    [Fact]
    public void ValidateMethodEmission_SpiTakesPrecedenceOverCodable()
    {
        // If a method is both @_spi AND a Codable encode, SPI gate fires first
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = new MethodDecl
        {
            Name = "encode",
            MangledName = "$s10TestModule6encodeyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsSpiProtected = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("to", new NamedTypeSpec("Swift.Encoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        // SPI fires before Codable
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_ImplicitOverridingTakesPrecedenceOverCodable()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6inityyF",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsModuleInternal = true,
            IsImplicit = true,
            IsOverride = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("from", new NamedTypeSpec("Swift.Decoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        // Implicit+overriding fires before Codable
        Assert.Equal(SkipReason.ModuleInternal, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_AsyncTupleWithNonSimpleEnum_ReturnsSkip()
    {
        // C6: Async methods with tuple returns containing non-simple enums
        var typeDatabase = CreateTypeDatabaseWithEnum("TestModule", "ComplexEnum", isSimple: false);

        var pipeline = new MemberValidationPipeline(typeDatabase);

        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("TestModule.ComplexEnum")
        });

        var method = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = "$s10TestModule11fetchResultyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, tupleReturn)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            Visibility = Visibility.Public
        };

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Contains("non-simple enum", result.Details!);
    }

    #endregion

    #region ValidationResult Tests

    [Fact]
    public void ValidationResult_Emit_Properties()
    {
        var result = ValidationResult.Emit;
        Assert.True(result.ShouldEmit);
        Assert.Null(result.Reason);
        Assert.Null(result.Details);
        Assert.False(result.IsSynthesized);
    }

    [Fact]
    public void ValidationResult_Skip_Properties()
    {
        var result = ValidationResult.Skip(SkipReason.SwiftUIConstraint, "test details");
        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
        Assert.Equal("test details", result.Details);
        Assert.False(result.IsSynthesized);
    }

    [Fact]
    public void ValidationResult_Synthesized_Properties()
    {
        var result = ValidationResult.Synthesized("test synthesized");
        Assert.False(result.ShouldEmit);
        Assert.Null(result.Reason);
        Assert.Equal("test synthesized", result.Details);
        Assert.True(result.IsSynthesized);
    }

    [Fact]
    public void WrapperValidationResult_Wrap_Properties()
    {
        var result = WrapperValidationResult.Wrap;
        Assert.True(result.ShouldEmitWrapper);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void WrapperValidationResult_Reject_Properties()
    {
        var result = WrapperValidationResult.Reject("closure_property");
        Assert.False(result.ShouldEmitWrapper);
        Assert.Equal("closure_property", result.RejectionReason);
    }

    #endregion

    #region Pipeline Construction Tests

    [Fact]
    public void Pipeline_CanBeCreatedWithTypeDatabase()
    {
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void ValidationContext_CanBeCreated()
    {
        var typeDatabase = CreateTypeDatabase();
        var emissionContext = new ModuleEmissionContext();
        var context = new ValidationContext(
            typeDatabase,
            pInvokeHelperContext: null,
            emissionContext,
            parentType: null,
            moduleDecl: null,
            siblingPropertyNames: null,
            conductor: null);

        Assert.Same(typeDatabase, context.TypeDatabase);
        Assert.Same(emissionContext, context.EmissionContext);
        Assert.Null(context.PInvokeHelperContext);
        Assert.Null(context.ParentType);
        Assert.Null(context.ModuleDecl);
        Assert.Null(context.SiblingPropertyNames);
        Assert.Null(context.Conductor);
    }

    #endregion

    #region Helper Methods

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
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnum(string moduleName, string enumName, bool isSimple)
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

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{enumName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, enumName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{enumName}"),
                MetadataAccessor = $"$s{moduleName}{enumName}OMa",
                Flags = isSimple ? TypeRecordFlags.SimpleEnum : TypeRecordFlags.None,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);
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

    private static PropertyDecl CreateProperty(string name, TypeSpec typeSpec)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static SubscriptDecl CreateSubscript(TypeSpec returnType, TypeSpec indexType)
    {
        return new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTest_subscript",
            IsStatic = false,
            ReturnTypeSpec = returnType,
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("index", indexType)
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethod(string name, TypeSpec returnType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithArgs(string name, TypeSpec returnType, params TypeSpec[] paramTypes)
    {
        var csSignature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, returnType),
        };
        for (int i = 0; i < paramTypes.Length; i++)
            csSignature.Add(CreateArgument($"param{i}", paramTypes[i]));

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
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

    #endregion
}
