// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MemberGateEvaluator — centralized gate logic for protocol and concrete-type member evaluation.
/// </summary>
public class MemberGateEvaluatorTests
{
    #region ContainsAnyTypeGenericArg Tests

    [Fact]
    public void ContainsAnyTypeGenericArg_StandaloneAnyType_ReturnsFalse()
    {
        Assert.False(MemberGateEvaluator.ContainsAnyTypeGenericArg("AnyType"));
    }

    [Fact]
    public void ContainsAnyTypeGenericArg_AnyTypeInGenericBrackets_ReturnsTrue()
    {
        Assert.True(MemberGateEvaluator.ContainsAnyTypeGenericArg("BatchedCollection<AnyType>"));
    }

    [Fact]
    public void ContainsAnyTypeGenericArg_AnyTypeAsPartOfLargerName_ReturnsFalse()
    {
        Assert.False(MemberGateEvaluator.ContainsAnyTypeGenericArg("BatchedCollection<MyAnyTypeModel>"));
    }

    [Fact]
    public void ContainsAnyTypeGenericArg_NestedGenericWithAnyType_ReturnsTrue()
    {
        Assert.True(MemberGateEvaluator.ContainsAnyTypeGenericArg("SwiftOptional<SwiftDictionary<AnyType, AnyType>>"));
    }

    [Fact]
    public void ContainsAnyTypeGenericArg_NoGenericBrackets_ReturnsFalse()
    {
        Assert.False(MemberGateEvaluator.ContainsAnyTypeGenericArg("System.Int64"));
    }

    [Fact]
    public void ContainsAnyTypeGenericArg_QualifiedAnyType_ReturnsTrue()
    {
        Assert.True(MemberGateEvaluator.ContainsAnyTypeGenericArg("SwiftArray<Swift.AnyType>"));
    }

    #endregion

    #region GateResult Tests

    [Fact]
    public void GateResult_Pass_IsEmittable()
    {
        var result = GateResult.Pass;
        Assert.True(result.IsEmittable);
        Assert.False(result.IsSkipped);
        Assert.False(result.IsInterfaceOnly);
        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void GateResult_Skipped_IsSkipped()
    {
        var result = GateResult.Skipped(SkipReason.UnsupportedSignature, "test");
        Assert.True(result.IsSkipped);
        Assert.False(result.IsEmittable);
        Assert.False(result.IsInterfaceOnly);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
    }

    [Fact]
    public void GateResult_SoftSkip_IsInterfaceOnly()
    {
        var result = GateResult.SoftSkip(SoftGateFlags.HasClosureParam);
        Assert.True(result.IsInterfaceOnly);
        Assert.True(result.IsEmittable);
        Assert.False(result.IsSkipped);
        Assert.Equal(SoftGateFlags.HasClosureParam, result.SoftFlags);
    }

    #endregion

    #region EvaluateProperty Tests

    [Fact]
    public void EvaluateProperty_NormalProperty_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("count", new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void EvaluateProperty_SwiftUIType_ReturnsSkipWithSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("body", new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateProperty_CombineType_ReturnsSkipWithSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("publisher", new NamedTypeSpec("Combine.Publisher"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateProperty_NonSwiftObjectBoundGeneric_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        // SwiftVoid doesn't implement ISwiftObject
        var voidParam = new NamedTypeSpec("Swift.Void");
        var delegateType = new NamedTypeSpec("TestModule.Delegate");
        delegateType.GenericParameters.Add(voidParam);

        var property = CreateProperty("handler", delegateType);

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsatisfiedGenericConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateProperty_ClosureInProtocolContext_ReturnsInterfaceOnly()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var closureType = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var property = CreateProperty("action", closureType);
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), protocolDecl);

        Assert.True(result.IsInterfaceOnly);
        Assert.Equal(SoftGateFlags.HasClosureProperty, result.SoftFlags);
    }

    [Fact]
    public void EvaluateProperty_ClosureWithoutProtocolContext_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var closureType = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var property = CreateProperty("action", closureType);

        // Without protocol context, closure soft gate is not applied
        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    #endregion

    #region EvaluateMethod Tests

    [Fact]
    public void EvaluateMethod_NormalMethod_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("process", new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), null);

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void EvaluateMethod_SwiftUIParam_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("render", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateMethod_ClosureParamInProtocol_ReturnsInterfaceOnly()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var closureArg = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var method = CreateMethod("setCallback", TupleTypeSpec.Empty, closureArg);
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), protocolDecl);

        Assert.True(result.IsInterfaceOnly);
        Assert.True(result.SoftFlags.HasFlag(SoftGateFlags.HasClosureParam));
    }

    [Fact]
    public void EvaluateMethod_ExistentialParamInProtocol_ReturnsInterfaceOnly()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SomeProtocol") });

        var method = CreateMethod("handle", TupleTypeSpec.Empty, existentialType);
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), protocolDecl);

        Assert.True(result.IsInterfaceOnly);
        Assert.True(result.SoftFlags.HasFlag(SoftGateFlags.HasExistentialParam));
    }

    [Fact]
    public void EvaluateMethod_ClosureAndExistentialParams_ReturnsBothFlags()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var closureArg = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SomeProtocol") });

        var method = CreateMethodWithArgs("handle", TupleTypeSpec.Empty, closureArg, existentialType);
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), protocolDecl);

        Assert.True(result.IsInterfaceOnly);
        Assert.True(result.SoftFlags.HasFlag(SoftGateFlags.HasClosureParam));
        Assert.True(result.SoftFlags.HasFlag(SoftGateFlags.HasExistentialParam));
    }

    [Fact]
    public void EvaluateMethod_SoftGatePlusHardGate_HardGateWins()
    {
        // Method has both a closure param (soft gate) and a SwiftUI param (hard gate)
        // Hard gate should win → Skip, not InterfaceOnly
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var closureArg = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };

        var method = CreateMethodWithArgs("render", TupleTypeSpec.Empty, closureArg, new NamedTypeSpec("SwiftUI.View"));
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), protocolDecl);

        // SwiftUI hard gate fires at M10, overriding the closure soft gate from M8
        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateMethod_NonISwiftObjectBoundGeneric_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var voidParam = new NamedTypeSpec("Swift.Void");
        var delegateType = new NamedTypeSpec("TestModule.Delegate");
        delegateType.GenericParameters.Add(voidParam);

        var method = CreateMethod("invoke", TupleTypeSpec.Empty, delegateType);

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsatisfiedGenericConstraint, result.Reason);
    }

    #endregion

    #region EvaluateSubscript Tests

    [Fact]
    public void EvaluateSubscript_NormalSubscript_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var subscript = CreateSubscript(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateSubscript(subscript, CreateModuleDecl("TestModule"), null);

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void EvaluateSubscript_SwiftUIReturnType_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var subscript = CreateSubscript(new NamedTypeSpec("SwiftUI.View"), new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateSubscript(subscript, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateSubscript_SwiftUIIndexParam_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var subscript = CreateSubscript(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateSubscript(subscript, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    #endregion

    #region EvaluateHardGates Tests

    [Fact]
    public void EvaluateHardGates_NormalMethod_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("process", new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateHardGates(method, CreateModuleDecl("TestModule"));

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void EvaluateHardGates_SwiftUIParam_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("render", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateHardGates(method, CreateModuleDecl("TestModule"));

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateHardGates_NeverReturnsInterfaceOnly()
    {
        // Hard gates mode should never produce InterfaceOnly
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        // Even with a closure param, hard gates return Emit (no soft gates)
        var closureArg = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var method = CreateMethod("callback", TupleTypeSpec.Empty, closureArg);

        var result = evaluator.EvaluateHardGates(method, CreateModuleDecl("TestModule"));

        Assert.False(result.IsInterfaceOnly);
    }

    [Fact]
    public void EvaluateHardGates_NonISwiftObjectBoundGeneric_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);

        var voidParam = new NamedTypeSpec("Swift.Void");
        var delegateType = new NamedTypeSpec("TestModule.Delegate");
        delegateType.GenericParameters.Add(voidParam);

        var method = CreateMethod("invoke", TupleTypeSpec.Empty, delegateType);

        var result = evaluator.EvaluateHardGates(method, CreateModuleDecl("TestModule"));

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsatisfiedGenericConstraint, result.Reason);
    }

    #endregion

    #region EvaluatePropertyHardGates Tests

    [Fact]
    public void EvaluatePropertyHardGates_NormalProperty_ReturnsPass()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("count", new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluatePropertyHardGates(property, CreateModuleDecl("TestModule"));

        Assert.Equal(GateDisposition.Emit, result.Disposition);
    }

    [Fact]
    public void EvaluatePropertyHardGates_SwiftUIType_ReturnsSkip()
    {
        var typeDatabase = CreateTypeDatabase();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("body", new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluatePropertyHardGates(property, CreateModuleDecl("TestModule"));

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    #endregion

    #region SwiftUI Database-Aware Gate Tests

    [Fact]
    public void EvaluateProperty_SwiftUIColorWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("primaryColor", new NamedTypeSpec("SwiftUI.Color"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluateProperty_SwiftUIFontWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("headlineFont", new NamedTypeSpec("SwiftUI.Font"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluateProperty_SwiftUIViewWithDatabase_StillSkipped()
    {
        // SwiftUI.View is NOT in the database — should still be rejected
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("body", new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateMethod_SwiftUIColorParamWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("setColor", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.Color"));

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluateMethod_SwiftUIViewParamWithDatabase_StillSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("render", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.View"));

        var result = evaluator.EvaluateMethod(method, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void EvaluateHardGates_SwiftUIColorWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var method = CreateMethod("setColor", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("SwiftUI.Color"));

        var result = evaluator.EvaluateHardGates(method, CreateModuleDecl("TestModule"));

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluatePropertyHardGates_SwiftUIColorWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("accentColor", new NamedTypeSpec("SwiftUI.Color"));

        var result = evaluator.EvaluatePropertyHardGates(property, CreateModuleDecl("TestModule"));

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluateSubscript_SwiftUIColorReturnWithDatabase_PassesThrough()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var subscript = CreateSubscript(new NamedTypeSpec("SwiftUI.Color"), new NamedTypeSpec("Swift.Int"));

        var result = evaluator.EvaluateSubscript(subscript, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsEmittable);
    }

    [Fact]
    public void EvaluateProperty_CombineTypeWithDatabase_StillSkipped()
    {
        // Combine.Publisher is NOT in any database — should still be rejected
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("publisher", new NamedTypeSpec("Combine.Publisher"));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
    }

    [Fact]
    public void ReferencesUnsupportedModule_SwiftUIColorWithDatabase_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var typeSpec = new NamedTypeSpec("SwiftUI.Color");

        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, typeDatabase));
    }

    [Fact]
    public void ReferencesUnsupportedModule_SwiftUIColorWithoutDatabase_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("SwiftUI.Color");

        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec));
    }

    [Fact]
    public void ReferencesUnsupportedModule_SwiftUIViewWithDatabase_ReturnsTrue()
    {
        // View is NOT in the database — still unsupported
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var typeSpec = new NamedTypeSpec("SwiftUI.View");

        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, typeDatabase));
    }

    [Fact]
    public void ReferencesUnsupportedModule_RegisteredContainerWithUnsupportedGenericArg_ReturnsTrue()
    {
        // SwiftUI.Binding is registered, but SwiftUI.View is NOT —
        // Binding<View> must still be rejected
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var typeSpec = new NamedTypeSpec("SwiftUI.Binding", new TypeSpec[] { new NamedTypeSpec("SwiftUI.View") });

        Assert.True(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, typeDatabase));
    }

    [Fact]
    public void ReferencesUnsupportedModule_RegisteredContainerWithRegisteredGenericArg_ReturnsFalse()
    {
        // Both Binding and Color are registered — Binding<Color> should pass
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var typeSpec = new NamedTypeSpec("SwiftUI.Binding", new TypeSpec[] { new NamedTypeSpec("SwiftUI.Color") });

        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, typeDatabase));
    }

    [Fact]
    public void ReferencesUnsupportedModule_RegisteredContainerWithNonSwiftUIArg_ReturnsFalse()
    {
        // Binding<Swift.String> — Binding registered, String from a supported module → pass
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var typeSpec = new NamedTypeSpec("SwiftUI.Binding", new TypeSpec[] { new NamedTypeSpec("Swift.String") });

        Assert.False(MemberEmissionValidator.ReferencesUnsupportedModule(typeSpec, typeDatabase));
    }

    [Fact]
    public void EvaluateProperty_RegisteredContainerWithUnsupportedGenericArg_StillSkipped()
    {
        // Full gate evaluation: Binding<View> should be skipped
        var typeDatabase = CreateTypeDatabaseWithSwiftUI();
        var evaluator = new MemberGateEvaluator(typeDatabase);
        var property = CreateProperty("content",
            new NamedTypeSpec("SwiftUI.Binding", new TypeSpec[] { new NamedTypeSpec("SwiftUI.View") }));

        var result = evaluator.EvaluateProperty(property, CreateModuleDecl("TestModule"), null);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.SwiftUIConstraint, result.Reason);
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

    private static ProtocolDecl CreateProtocolDecl(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}Mp",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Operators = new List<OperatorDecl>(),
            Types = new List<TypeDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
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

    private static MethodDecl CreateMethod(string name, TypeSpec returnType, TypeSpec paramType = null)
    {
        var csSignature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, returnType),
        };
        if (paramType != null)
            csSignature.Add(CreateArgument("param", paramType));

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

    /// <summary>
    /// Creates a type database with SwiftUI types registered,
    /// matching the production SwiftUIDatabase.xml entries (Color, Font, EdgeInsets,
    /// Animation, Image, Text, AnyView, Binding).
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithSwiftUI()
    {
        var typeDatabase = CreateTypeDatabase();
        var swiftUIModule = new ModuleTypeDatabase("SwiftUI", "/System/Library/Frameworks/SwiftUI.framework/SwiftUI");
        foreach (var typeName in new[] { "Color", "Font", "EdgeInsets", "Animation", "Image", "Text", "AnyView", "Binding" })
        {
            swiftUIModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"SwiftUI.{typeName}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("SwiftUI", typeName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"SwiftUI.{typeName}"),
                    MetadataAccessor = $"$s7SwiftUI{typeName.Length}{typeName}VMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct
                });
        }
        typeDatabase.AddModuleDatabase(swiftUIModule);
        return typeDatabase;
    }

    #endregion
}
