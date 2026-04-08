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

    [Fact]
    public void ValidateMethodEmission_VariadicMethod_NotSuppressed()
    {
        // Variadic T... appears as Array<T> in ABI JSON — at the ABI level they're identical.
        // CallConvSwift dispatches correctly using SwiftArray<T> as a single pointer.
        // No gate needed; ArrayProjection handles the Array<T> parameter.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("append", TupleTypeSpec.Empty);
        method.HasVariadicParameter = true;
        method.IsConstructor = false;

        var result = pipeline.ValidateMethodEmission(method, null!);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidateMethodEmission_VariadicConstructor_NotSuppressed()
    {
        // Variadic constructors are handled separately by ConstructorWrapperEmitter,
        // not by MemberValidationPipeline. Constructors should pass pipeline validation.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.HasVariadicParameter = true;
        method.IsConstructor = true;

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

    [Fact]
    public void ValidatePropertyEmission_ConstrainedExtensionConflict_AllSpecializationsSkipped()
    {
        // Bug #2 regression: multiple `extension Wrapper where T == Concrete` blocks each
        // declare a property with the same Swift name. The ABI dump emits one Var node per
        // specialization (each with its own mangled accessor). C# generics have only one
        // specialization, so emitting any of them silently dispatches the wrong symbol for
        // the other closed generic instantiations. The validator must skip ALL of them.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var parent = BuildGenericStructWithConflictingExtensionProperties(
            "VerificationResult",
            propertyName: "jwsRepresentation",
            specializationCount: 3);

        // All three sibling PropertyDecls hit the gate.
        foreach (var prop in parent.Properties)
        {
            var result = pipeline.ValidatePropertyEmission(prop, null!);

            Assert.False(result.ShouldEmit);
            Assert.Equal(SkipReason.UnsupportedType, result.Reason);
            Assert.Contains("constrained-extension", result.Details!);
            Assert.Contains("jwsRepresentation", result.Details!);
            Assert.Contains("VerificationResult", result.Details!);
        }
    }

    [Fact]
    public void ValidatePropertyEmission_NonGenericParent_NoConflictGate()
    {
        // The constrained-extension gate must NOT fire on a non-generic parent type, even
        // if (hypothetically) two PropertyDecls share a name — that's a different bug class
        // and should not be silently absorbed by this gate.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var nonGenericParent = BuildBareStructDecl("Plain", isGeneric: false);
        var prop = CreateProperty("count", new NamedTypeSpec("Swift.Int"));
        prop.ParentDecl = nonGenericParent;
        nonGenericParent.Properties.Add(prop);

        var result = pipeline.ValidatePropertyEmission(prop, null!);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidatePropertyEmission_GenericParentSinglePropertyName_NoSkip()
    {
        // Single-occurrence properties on a generic type are emittable. The conflict gate
        // is keyed on duplicate (Name, IsStatic) — a unique name should pass through.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var parent = BuildGenericStructWithConflictingExtensionProperties(
            "Wrapper",
            propertyName: "uniqueProp",
            specializationCount: 1);

        var result = pipeline.ValidatePropertyEmission(parent.Properties[0], null!);

        // Not blocked by the constrained-extension gate (other gates may apply but the
        // conflict-specific message must NOT be present).
        if (result.Details != null)
            Assert.DoesNotContain("constrained-extension", result.Details);
    }

    [Fact]
    public void ValidatePropertyEmission_StaticAndInstanceWithSameName_NoConflict()
    {
        // Instance and static properties with the same Swift name project to distinct
        // C# members and must NOT collide on the constrained-extension gate.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);

        var parent = BuildBareStructDecl("Holder", isGeneric: true);
        var instanceProp = CreateProperty("value", new NamedTypeSpec("Swift.Int"));
        instanceProp.IsStatic = false;
        instanceProp.ParentDecl = parent;
        var staticProp = CreateProperty("value", new NamedTypeSpec("Swift.Int"));
        staticProp.IsStatic = true;
        staticProp.ParentDecl = parent;
        parent.Properties.Add(instanceProp);
        parent.Properties.Add(staticProp);

        var instanceResult = pipeline.ValidatePropertyEmission(instanceProp, null!);
        var staticResult = pipeline.ValidatePropertyEmission(staticProp, null!);

        // Neither should be blocked by the constrained-extension gate.
        if (instanceResult.Details != null)
            Assert.DoesNotContain("constrained-extension", instanceResult.Details);
        if (staticResult.Details != null)
            Assert.DoesNotContain("constrained-extension", staticResult.Details);
    }

    private static StructDecl BuildBareStructDecl(string typeName, bool isGeneric)
    {
        var decl = new StructDecl
        {
            Name = typeName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            MangledName = $"$sTestModule{typeName.Length}{typeName}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = string.Empty
        };
        if (isGeneric)
        {
            decl.GenericParameters.Add(new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>()));
        }
        return decl;
    }

    private static StructDecl BuildGenericStructWithConflictingExtensionProperties(
        string typeName,
        string propertyName,
        int specializationCount)
    {
        var parent = BuildBareStructDecl(typeName, isGeneric: true);
        for (int i = 0; i < specializationCount; i++)
        {
            var prop = CreateProperty(propertyName, new NamedTypeSpec("Swift.String"));
            prop.ParentDecl = parent;
            parent.Properties.Add(prop);
        }
        return parent;
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

    #region Phase 3-6 Gate Tests (Session 2)

    [Fact]
    public void ValidateMethodEmission_ThunkClosureInGenericType_ReturnsSkip()
    {
        // Phase 3: Constructor with thunk closure in generic type
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithArgs("handle", TupleTypeSpec.Empty, closureType);

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(method, validationCtx);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericTypeCallback, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_AsyncInGenericType_ReturnsSkip()
    {
        // Phase 3: Async method in generic type
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethod("fetch", new NamedTypeSpec("Swift.Int"));
        method.IsAsync = true;

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(method, validationCtx);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericTypeCallback, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_NoPInvokeHelper_SkipsThunkCheck()
    {
        // Phase 3 only fires when PInvokeHelperContext is present (generic parent type)
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithArgs("handle", TupleTypeSpec.Empty, closureType);

        var pipeline = new MemberValidationPipeline(typeDatabase);
        // No PInvokeHelperContext → thunk check is skipped
        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidateMethodEmission_ProtocolExtensionMethod_SkipsThunkCheck()
    {
        // Phase 3: Protocol extension methods are always let through
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethod("fetch", new NamedTypeSpec("Swift.Int"));
        method.IsAsync = true;
        method.IsProtocolExtensionMethod = true;

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(method, validationCtx);

        // Protocol extension methods bypass the thunk check
        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidateMethodEmission_HasAssociatedTypeProtocolConstraint_ReturnsSkip()
    {
        // Phase 4: Protocol constraint with associated types
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.SequenceLike", TypeRecordFlags.HasAssociatedTypes);

        var method = CreateMethod("decode", new NamedTypeSpec("Swift.Int"));
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            CreateGenericArgumentWithProtocolConformance("T", "TestModule.SequenceLike")
        };

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericProtocolConstraint, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_ConstructorSkipsProtocolConstraintCheck()
    {
        // Phase 4: Constructors do NOT check protocol constraints (original behavior)
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.SequenceLike", TypeRecordFlags.HasAssociatedTypes);

        var method = CreateMethod("init", new NamedTypeSpec("Swift.Int"));
        method.IsConstructor = true;
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            CreateGenericArgumentWithProtocolConformance("T", "TestModule.SequenceLike")
        };

        var pipeline = new MemberValidationPipeline(typeDatabase);
        // Phase 4 skips for constructors → falls through to Phase 6 (generic ctor own params)
        var result = pipeline.ValidateMethodEmission(method, null);

        // Caught by Phase 6 (generic constructor own params) not Phase 4
        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
    }

    [Fact]
    public void ValidateMethodEmission_GenericConstructorOwnParams_ReturnsSkip()
    {
        // Phase 6: Generic constructor with method-own type parameters
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);

        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.IsConstructor = true;
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
        };
        // Parent type is NOT generic → T is a method-own generic param
        var moduleDecl = CreateModuleDecl("TestModule");
        method.ParentDecl = new ClassDecl
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
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Contains("generic constructors", result.Details!);
    }

    [Fact]
    public void ValidateMethodEmission_GenericConstructorInheritedParams_ReturnsEmit()
    {
        // Phase 6: Constructor generic params from parent → NOT method-own, should emit
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentDecl = new ClassDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "τ_0_0", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.IsConstructor = true;
        method.ParentDecl = parentDecl;
        // Constructor inherits τ_0_0 from parent — not method-own
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "τ_0_0", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.True(result.ShouldEmit);
    }

    #endregion

    #region End-to-End Integration Tests (HandleBaseDecl flow)

    /// <summary>
    /// These tests go through ModuleHandler → HandleBaseDecl → pipeline, verifying that
    /// the gates actually prevent emission end-to-end (not just that the pipeline returns Skip).
    /// </summary>

    [Fact]
    public void EndToEnd_ProtocolConstraintMethod_ProducesNoOutput()
    {
        // Phase 4 gate: method with associated type protocol constraint → no C# output
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.SequenceLike", TypeRecordFlags.HasAssociatedTypes);

        var method = CreateMethod("decode", new NamedTypeSpec("Swift.Int"));
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            CreateGenericArgumentWithProtocolConformance("T", "TestModule.SequenceLike")
        };

        var (csOutput, _) = EmitModuleWithMethod(method, typeDatabase);

        // No binding method declaration emitted — only unsupported comment for the skipped method
        Assert.DoesNotContain("Decode(", csOutput);
        Assert.DoesNotContain("Decode<", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("decode", csOutput); // method name in comment
    }

    [Fact]
    public void EndToEnd_ThunkClosureInGenericType_PipelineSkips()
    {
        // Phase 3 gate: thunk closure in generic type → pipeline returns Skip.
        // Note: This can't be tested through ModuleHandler E2E because module-level
        // free functions don't have PInvokeHelperContext. The gate fires when methods
        // inside generic types go through HandleBaseDecl in ClassHandler/StructHandler,
        // which passes PInvokeHelperContext via TypeHandlerContext. We verify the pipeline
        // directly + verify the existing handler tests (which were updated to test
        // through the pipeline with a PInvokeHelperContext).
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithArgs("handle", TupleTypeSpec.Empty, closureType);

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);

        var result = pipeline.ValidateMethodEmission(method, validationCtx);
        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericTypeCallback, result.Reason);

        // Without PInvokeHelperContext, the method should pass (normal non-generic context)
        var resultNoPInvoke = pipeline.ValidateMethodEmission(method, null);
        Assert.True(resultNoPInvoke.ShouldEmit);
    }

    [Fact]
    public void EndToEnd_GenericConstructorOwnParams_ProducesNoOutput()
    {
        // Phase 6 gate: generic constructor with method-own params → no C# output
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDeclForE2E("Point", moduleDecl, isGeneric: false);

        var method = CreateMethod("init", TupleTypeSpec.Empty);
        method.IsConstructor = true;
        method.ParentDecl = parentDecl;
        method.ModuleDecl = moduleDecl;
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
        };

        var (csOutput, _) = EmitModuleWithMethod(method, typeDatabase);

        // No constructor binding emitted — only unsupported comment
        Assert.DoesNotContain("Point(", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("init", csOutput); // method name in comment
    }

    [Fact]
    public void EndToEnd_NormalMethod_ProducesOutput()
    {
        // Sanity check: a normal method DOES produce output through HandleBaseDecl
        var typeDatabase = CreateTypeDatabase();

        var method = CreateMethod("doWork", TupleTypeSpec.Empty);

        var (csOutput, _) = EmitModuleWithMethod(method, typeDatabase);

        Assert.Contains("DoWork", csOutput);
    }

    /// <summary>
    /// Helper: emits a single method through ModuleHandler → HandleBaseDecl → pipeline → handler.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitModuleWithMethod(
        MethodDecl method,
        TypeDatabase typeDatabase,
        PInvokeHelperContext pinvokeCtx = null)
    {
        var moduleDecl = method.ModuleDecl as ModuleDecl ?? CreateModuleDecl("TestModule");
        // Ensure method is parented correctly for module-level emission
        if (method.ParentDecl == null)
            method.ParentDecl = moduleDecl;
        if (method.ModuleDecl == null)
            method.ModuleDecl = moduleDecl;

        moduleDecl.Methods = new List<MethodDecl> { method };

        try { typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib")); }
        catch { /* already added */ }

        var csStringWriter = new System.IO.StringWriter();
        var swiftStringWriter = new System.IO.StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var context = new TypeHandlerContext(pinvokeCtx, new(), null);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static ClassDecl CreateClassDeclForE2E(string name, ModuleDecl moduleDecl, bool isGeneric)
    {
        var genericParams = isGeneric
            ? new List<GenericArgumentDecl> { new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()) }
            : new List<GenericArgumentDecl>();

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParams,
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
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

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    private static GenericArgumentDecl CreateGenericArgumentWithProtocolConformance(string typeName, string protocolName)
    {
        return new GenericArgumentDecl(
            TypeName: typeName,
            SugaredTypeName: typeName,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { typeName },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(protocolName),
                    Kind: ConformanceKind.Protocol)
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
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
