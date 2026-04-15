// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EveryProtocolEmitter Swift code generation.
/// </summary>
public class EveryProtocolEmitterTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly EveryProtocolEmitter _emitter;

    public EveryProtocolEmitterTests()
    {
        _typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        _typeDatabase.AddModuleDatabase(module);
        _emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
    }

    #region EveryProtocol Class Emission Tests

    [Fact]
    public void EmitEveryProtocolClass_GeneratesClassDeclaration()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public final class EveryProtocol", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleProperty()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("var handle: UnsafeRawPointer?", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesDefaultInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init()", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init(handle: UnsafeRawPointer)", output);
    }

    #endregion

    #region Protocol Vtable Struct Emission Tests

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesStructDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("fileprivate struct TestProtocol_vtable", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesCsVTHandle()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var csVTHandle: OpaquePointer?", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertyGetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_get:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertySetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_set:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesMethodField()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_doSomething_0:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesVtableInstance()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("private var _testProtocol_vtable = TestProtocol_vtable()", output);
    }

    #endregion

    #region Protocol Extension Emission Tests

    [Fact]
    public void EmitProtocolExtension_GeneratesExtensionDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertyGetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var value:", output);
        Assert.Contains("get {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertySetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("set {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesMethodImplementation()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doSomething()", output);
    }

    #endregion

    #region SetVtable Function Emission Tests

    [Fact]
    public void EmitSetVtableFunction_GeneratesSilgenName()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("@_silgen_name(\"SetTestProtocol_vtable\")", output);
    }

    [Fact]
    public void EmitSetVtableFunction_GeneratesPublicFunction()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("public func setTestProtocol_vtable(uvt: UnsafeRawPointer)", output);
    }

    [Fact]
    public void EmitSetVtableFunction_CopiesVtable()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("_testProtocol_vtable = vt.pointee", output);
    }

    #endregion

    #region Protocol Conformance Filtering Tests

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolsWithSelfRequirement()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.HasSelfRequirement = true;

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
    }

    [Fact]
    public void EmitProtocolConformance_GeneratesTypealiasForAssociatedTypes()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
        Assert.Contains("public typealias Element = Any", output);
    }

    [Fact]
    public void EmitProtocolConformance_GeneratesMultipleTypealiases()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Key" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Value" });

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("public typealias Key = Any", output);
        Assert.Contains("public typealias Value = Any", output);
    }

    [Fact]
    public void EmitProtocolConformance_EmptyMarkerProtocol_EmitsTrivialConformance()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        // Empty marker protocol (no members at all) — gets trivial conformance
        // for existential container creation (e.g., Taggable)

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_WithGlobalSignatureConflict_SkipsSecondMethodImplementation()
    {
        var firstProtocol = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var secondProtocol = CreateProtocolWithMethod("SecondProtocol", "conflict");
        var globalSignatures = new HashSet<string>();

        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);

        Assert.Contains("public func conflict()", firstOutput);
        Assert.DoesNotContain("public func conflict()", secondOutput);
        Assert.Contains("extension EveryProtocol: TestModule.SecondProtocol", secondOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolWithConstructorRequirements()
    {
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");
        protocolDecl.Methods.Add(CreateMethodDecl("instanceMethod"));
        protocolDecl.Methods.Add(CreateMethodDecl("utility", methodType: MethodType.Static));
        protocolDecl.Methods.Add(CreateMethodDecl("init", isConstructor: true));

        var output = EmitFullConformance(protocolDecl);

        // Entire conformance skipped because protocol has constructor requirements
        Assert.Empty(output);
    }

    [Fact]
    public void EmitProtocolExtension_WithProtocolCompositionProperty_UsesAnyCompositionSyntax()
    {
        var protocolDecl = CreateSimpleProtocol("Composable");
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "delegate",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[]
            {
                new NamedTypeSpec("TestModule.P1"),
                new NamedTypeSpec("TestModule.P2")
            }),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("delegate_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var delegate: any TestModule.P1 & TestModule.P2", output);
    }

    [Fact]
    public void EmitProtocolExtension_WithGenericTypeParameter_EmitsSelfTypedStub()
    {
        // "T" is treated as protocol-level generic param (Self) by TypeSpecHelpers.
        // Self-typed properties get fatalError() stubs with EveryProtocol substitution.
        var protocolDecl = CreateSimpleProtocol("GenericLike");
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "item",
            SwiftTypeSpec = new NamedTypeSpec("T"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("item_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var item: EveryProtocol", output);
        Assert.Contains("Self-typed property 'item'", output);
    }

    [Fact]
    public void EmitProtocolExtension_WithEscapingAsyncThrowingClosure_FormatsClosureType()
    {
        var protocolDecl = CreateSimpleProtocol("ClosureLike");
        var closure = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: new NamedTypeSpec("Swift.String"));
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        closure.IsAsync = true;
        closure.Throws = true;

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "callback",
            SwiftTypeSpec = closure,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("callback_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // @escaping is excluded from property type declarations (only valid on function parameters).
        // Other attributes like @MainActor, @Sendable would be preserved.
        Assert.Contains("public var callback: (Swift.Int) async throws -> Swift.String", output);
    }

    [Fact]
    public void EmitProtocolExtension_ThrowingMethod_EmitsThrowsKeyword()
    {
        var protocolDecl = CreateSimpleProtocol("Throwable");
        protocolDecl.Methods.Add(CreateMethodDecl("doWork", throws: true));

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doWork() throws", output);
    }

    [Fact]
    public void EmitProtocolExtension_ThrowingMethodWithReturn_EmitsThrowsBeforeArrow()
    {
        var protocolDecl = CreateSimpleProtocol("Throwable");
        var method = CreateMethodDecl("makeItem", throws: true);
        // Replace return type with a non-void type
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocolDecl.Methods.Add(method);

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func makeItem() throws -> Swift.Int", output);
    }

    [Fact]
    public void EmitProtocolExtension_NonThrowingMethod_NoThrowsKeyword()
    {
        var protocolDecl = CreateProtocolWithMethod("NonThrowing", "doWork");

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doWork()", output);
        Assert.DoesNotContain("throws", output);
    }

    #endregion

    #region Witness Table Getter Tests

    [Fact]
    public void EmitWitnessTableGetter_GeneratesSilgenName()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        Assert.Contains("@_silgen_name(\"Get_EveryProtocol_TestProtocol_WitnessTable\")", output);
    }

    [Fact]
    public void EmitWitnessTableGetter_GeneratesPublicFunction()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        Assert.Contains("public func getEveryProtocolTestProtocolWitnessTable() -> UnsafeRawPointer", output);
    }

    [Fact]
    public void EmitWitnessTableGetter_UsesCorrectProtocolName()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        Assert.Contains("any TestModule.TestProtocol = instance", output);
    }

    [Fact]
    public void EmitTypeMetadataGetter_GeneratesSilgenName()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitTypeMetadataGetter(writer);
        var output = stringWriter.ToString();

        Assert.Contains("@_silgen_name(\"Get_EveryProtocol_TypeMetadata\")", output);
    }

    [Fact]
    public void EmitTypeMetadataGetter_ReturnsUnsafeRawPointer()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitTypeMetadataGetter(writer);
        var output = stringWriter.ToString();

        Assert.Contains("public func getEveryProtocolTypeMetadata() -> UnsafeRawPointer", output);
    }

    #endregion

    #region Vtable Index Integrity Tests (Bug #21)

    [Fact]
    public void EmitProtocolConformance_WithGlobalSkip_VtableFieldIndicesMatchExtensionReferences()
    {
        // Protocol has 3 methods: start, update, finish
        // "update" is globally skipped (already emitted by another protocol)
        // Vtable declares: func_start_0, func_update_1, func_finish_2
        // Extension must reference func_finish_2 (not func_finish_1)
        var firstProtocol = CreateProtocolWithMethod("FirstProtocol", "update");
        var secondProtocol = CreateSimpleProtocol("SecondProtocol");
        secondProtocol.Methods.Add(CreateMethodDecl("start"));
        secondProtocol.Methods.Add(CreateMethodDecl("update"));
        secondProtocol.Methods.Add(CreateMethodDecl("finish"));

        var globalSignatures = new HashSet<string>();

        // First protocol claims "update()"
        EmitFullConformance(firstProtocol, globalSignatures);

        // Second protocol emits with global dedup — "update" skipped
        var vtableOutput = EmitVtableStruct(secondProtocol);
        var extensionOutput = EmitFullConformance(secondProtocol, globalSignatures);

        // Vtable struct must declare all 3 fields sequentially
        Assert.Contains("func_start_0", vtableOutput);
        Assert.Contains("func_update_1", vtableOutput);
        Assert.Contains("func_finish_2", vtableOutput);

        // Extension must use matching indices for emitted methods
        Assert.Contains("func_start_0", extensionOutput);
        Assert.DoesNotContain("public func update()", extensionOutput); // globally skipped
        Assert.Contains("func_finish_2", extensionOutput);
        // Must NOT have func_finish_1 (the drifted index from the old bug)
        Assert.DoesNotContain("func_finish_1", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_WithMultipleGlobalSkips_IndicesStillMatch()
    {
        // Protocol has 4 methods: a, b, c, d
        // "b" and "c" are globally skipped
        // Vtable declares: func_a_0, func_b_1, func_c_2, func_d_3
        // Extension must reference func_d_3 (not func_d_1)
        var proto1 = CreateProtocolWithMethod("Proto1", "b");
        var proto2 = CreateProtocolWithMethod("Proto2", "c");
        var proto3 = CreateSimpleProtocol("Proto3");
        proto3.Methods.Add(CreateMethodDecl("a"));
        proto3.Methods.Add(CreateMethodDecl("b"));
        proto3.Methods.Add(CreateMethodDecl("c"));
        proto3.Methods.Add(CreateMethodDecl("d"));

        var globalSignatures = new HashSet<string>();
        EmitFullConformance(proto1, globalSignatures);
        EmitFullConformance(proto2, globalSignatures);

        var vtableOutput = EmitVtableStruct(proto3);
        var extensionOutput = EmitFullConformance(proto3, globalSignatures);

        // Vtable declares all 4 fields
        Assert.Contains("func_a_0", vtableOutput);
        Assert.Contains("func_b_1", vtableOutput);
        Assert.Contains("func_c_2", vtableOutput);
        Assert.Contains("func_d_3", vtableOutput);

        // Extension emits a and d, skips b and c
        Assert.Contains("func_a_0", extensionOutput);
        Assert.DoesNotContain("public func b()", extensionOutput);
        Assert.DoesNotContain("public func c()", extensionOutput);
        Assert.Contains("func_d_3", extensionOutput);
        Assert.DoesNotContain("func_d_1", extensionOutput);
        Assert.DoesNotContain("func_d_2", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_NoGlobalSkips_IndicesSequential()
    {
        // Baseline: without any global conflicts, indices are simply sequential
        var protocol = CreateSimpleProtocol("Sequential");
        protocol.Methods.Add(CreateMethodDecl("alpha"));
        protocol.Methods.Add(CreateMethodDecl("beta"));
        protocol.Methods.Add(CreateMethodDecl("gamma"));

        var vtableOutput = EmitVtableStruct(protocol);
        var extensionOutput = EmitFullConformance(protocol);

        Assert.Contains("func_alpha_0", vtableOutput);
        Assert.Contains("func_beta_1", vtableOutput);
        Assert.Contains("func_gamma_2", vtableOutput);

        Assert.Contains("func_alpha_0", extensionOutput);
        Assert.Contains("func_beta_1", extensionOutput);
        Assert.Contains("func_gamma_2", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_ThrowingFirstThenNonThrowing_EmitsNonThrowingWithOverride()
    {
        // Protocol A has "process()" as throwing, Protocol B has it as non-throwing.
        // In Swift, a non-throwing func satisfies both requirements, but a throwing func
        // does NOT satisfy a non-throwing requirement. The non-throwing variant must win.
        var throwingProtocol = CreateSimpleProtocol("ThrowingProto");
        throwingProtocol.Methods.Add(CreateMethodDecl("process", throws: true));

        var nonThrowingProtocol = CreateSimpleProtocol("NonThrowingProto");
        nonThrowingProtocol.Methods.Add(CreateMethodDecl("process", throws: false));

        // Build the non-throwing overrides set (simulates what ModuleHandler.ComputeNonThrowingOverrides does)
        // Uses full signature format: name(params)->ReturnType
        var nonThrowingOverrides = new HashSet<string> { "process()->Void" };
        var globalSignatures = new HashSet<string>();

        // Throwing protocol emitted first — but the override forces non-throwing
        var throwingOutput = EmitFullConformance(throwingProtocol, globalSignatures, nonThrowingOverrides);

        // The emitted method should NOT have "throws" because the override suppresses it
        Assert.Contains("public func process()", throwingOutput);
        Assert.DoesNotContain("throws", throwingOutput);
    }

    [Fact]
    public void EmitProtocolConformance_NonThrowingOnlySignature_NoOverrideNeeded()
    {
        // When a method is only non-throwing (no conflict), it should emit normally
        var protocol = CreateSimpleProtocol("SimpleProto");
        protocol.Methods.Add(CreateMethodDecl("process", throws: false));

        var nonThrowingOverrides = new HashSet<string>(); // empty — no conflicts
        var globalSignatures = new HashSet<string>();

        var output = EmitFullConformance(protocol, globalSignatures, nonThrowingOverrides);

        Assert.Contains("public func process()", output);
        Assert.DoesNotContain("throws", output);
    }

    [Fact]
    public void EmitProtocolConformance_ThrowingOnlySignature_EmitsThrowsNormally()
    {
        // When a method is only throwing (no conflict), it should emit with throws
        var protocol = CreateSimpleProtocol("ThrowingOnly");
        protocol.Methods.Add(CreateMethodDecl("process", throws: true));

        var nonThrowingOverrides = new HashSet<string>(); // empty — no conflicts
        var globalSignatures = new HashSet<string>();

        var output = EmitFullConformance(protocol, globalSignatures, nonThrowingOverrides);

        Assert.Contains("public func process() throws", output);
    }

    #endregion

    #region Underscore Parameter Name Tests (Issue I)

    [Fact]
    public void EmitProtocolExtension_UnderscorePrivateName_DoesNotReferenceUnderscore()
    {
        // When a protocol method has _ as the internal parameter name (PrivateName),
        // the emitter should NOT generate "var _Copy = _" (which is illegal in Swift).
        // Instead, it should fall back to using the public Name as the internal name.
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "for",
            PrivateName = "_",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageAsset"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should NOT contain "var _Copy = _" (the broken pattern)
        Assert.DoesNotContain("var _Copy = _", output);
        // Should NOT try to use _ as a value
        Assert.DoesNotContain("= _\n", output);
    }

    [Fact]
    public void EmitProtocolExtension_BothNamesUnderscore_UsesSyntheticName()
    {
        // When both Name and PrivateName are _, use synthetic arg0
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "_",
            PrivateName = "_",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should use synthetic name, not _
        Assert.DoesNotContain("var _Copy", output);
        // Index starts at 1 (skipping return type at index 0)
        Assert.Contains("arg1", output);
    }

    [Fact]
    public void EmitProtocolExtension_NormalParamName_PreservesName()
    {
        // Normal parameter names should still work as before
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "for",
            PrivateName = "asset",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageAsset"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("asset", output);
    }

    #endregion

    #region Optional Closure Property Tests (EC-15)

    [Fact]
    public void EmitProtocolExtension_OptionalClosureProperty_EmitsCorrectType()
    {
        // Protocol with optional closure property: var onDismiss: ((SomeType) -> Void)?
        var protocolDecl = CreateSimpleProtocol("DismissDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeType") }),
            returnType: TupleTypeSpec.Empty);
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "onDismiss",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("onDismiss_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("onDismiss_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // Optional closure properties get fatalError() stubs (can't dispatch through vtable)
        Assert.Contains("public var onDismiss:", output);
        Assert.Contains("fatalError", output);
        Assert.Contains("closure property 'onDismiss' cannot be dispatched", output);
    }

    [Fact]
    public void EmitProtocolExtension_MainActorOptionalClosureProperty_PreservesMainActor()
    {
        // Protocol with @MainActor optional closure property:
        // var callback: (@MainActor (SomeType) -> Void)?
        var protocolDecl = CreateSimpleProtocol("CallbackDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeType") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "callback",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("callback_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // @MainActor should be preserved in the property type
        Assert.Contains("@MainActor", output);
        // @escaping should NOT appear
        Assert.DoesNotContain("@escaping", output);
    }

    [Fact]
    public void EmitProtocolExtension_EscapingOptionalClosureProperty_ExcludesEscaping()
    {
        // Protocol with @escaping optional closure property:
        // var handler: (@escaping (Int) -> Void)?
        // @escaping should be stripped since it's invalid on property declarations
        var protocolDecl = CreateSimpleProtocol("HandlerDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "handler",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("handler_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("handler_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.DoesNotContain("@escaping", output);
        Assert.Contains("public var handler:", output);
    }

    #endregion

    #region Method-Level Generic Protocol Gate Tests

    [Fact]
    public void EmitConformance_AllMethodsHaveMethodLevelGenerics_EmitsConformance()
    {
        // Like Swinject.Resolver: all methods have method-level generics only (τ_1_0),
        // no properties — should emit EveryProtocol conformance with stubs
        var protocol = CreateSimpleProtocol("Resolver");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("resolve"));
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("resolveWithArg"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Resolver", output);
    }

    [Fact]
    public void EmitConformance_MethodLevelGenericsWithNonGenericProperty_EmitsConformance()
    {
        // Like RxSwift.SchedulerType: has method-level generics AND a non-generic property.
        // Mixed-generic protocol: ALL members get fatalError() stubs because the type
        // projection pipeline generates incorrect types for non-generic members.
        var protocol = CreateSimpleProtocol("SchedulerType");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("scheduleRelative"));
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "now",
            SwiftTypeSpec = new NamedTypeSpec("Foundation.Date"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("now_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.SchedulerType", output);
        // Generic method gets a fatalError() stub
        Assert.Contains("fatalError", output);
        // Property also gets a stub (mixed-generic protocol — no vtable dispatch)
        Assert.DoesNotContain("func_now_get", output);
        Assert.Contains("public var now: Foundation.Date", output);
    }

    [Fact]
    public void EmitConformance_MethodLevelGenericsWithNonGenericMethod_EmitsConformance()
    {
        // Protocol has both generic and non-generic methods.
        // Mixed-generic protocol: ALL members get fatalError() stubs.
        var protocol = CreateSimpleProtocol("MixedProtocol");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("genericMethod"));
        protocol.Methods.Add(CreateMethodDecl("nonGenericMethod"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.MixedProtocol", output);
        // Generic method gets a fatalError() stub
        Assert.Contains("method-level generic method 'genericMethod'", output);
        // Non-generic method also gets a stub (mixed-generic protocol — no vtable dispatch)
        Assert.DoesNotContain("func_nonGenericMethod_1", output);
        Assert.Contains("func nonGenericMethod()", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithTau1_ReturnsTrue()
    {
        var method = CreateMethodWithMethodLevelGeneric("resolve");

        Assert.True(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithTau0_ReturnsFalse()
    {
        // τ_0_0 is Self (protocol-level) — not method-level only
        var method = CreateMethodDecl("selfMethod");
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        Assert.False(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithNoGenerics_ReturnsFalse()
    {
        var method = CreateMethodDecl("plainMethod");

        Assert.False(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void EmitConformance_GenericStubWithAsyncClosure_EmitsAsyncInSignature()
    {
        // Protocol with method-level generic that takes an async closure:
        // func process<T>(_ body: @Sendable (T) async -> Void)
        var protocol = CreateSimpleProtocol("AsyncProcessor");
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("τ_1_0"),
            TupleTypeSpec.Empty)
        {
            IsAsync = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("Sendable"));
        var method = CreateMethodWithSignature("process", closureType);
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.AsyncProcessor", output);
        Assert.Contains("async", output);
        Assert.Contains("@Sendable", output);
    }

    [Fact]
    public void EmitConformance_GenericStubWithLabeledTuple_PreservesLabels()
    {
        // func transform<T>(_ value: (item: T, count: Int))
        var elem0 = new NamedTypeSpec("τ_1_0") { TypeLabel = "item" };
        var elem1 = new NamedTypeSpec("Swift.Int") { TypeLabel = "count" };
        var tupleType = new TupleTypeSpec(new TypeSpec[] { elem0, elem1 });
        var method = CreateMethodWithSignature("transform", tupleType);
        var protocol = CreateSimpleProtocol("LabeledTupleProto");
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.LabeledTupleProto", output);
        Assert.Contains("item: _G0", output);
        Assert.Contains("count: Swift.Int", output);
    }

    [Fact]
    public void EmitConformance_GenericStubWithTupleParam_EmitsParenthesizedTuple()
    {
        // Protocol with method-level generic that takes a tuple:
        // func transform<T>(_ value: (T, Int)) -> (T, Int)
        var tupleType = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("τ_1_0"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = CreateMethodWithSignatureAndReturn("transform", tupleType, tupleType);
        var protocol = CreateSimpleProtocol("TupleTransformer");
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.TupleTransformer", output);
        // Tuple must be parenthesized: (T, Int), not bare T, Int
        Assert.Matches(@"\(_G0,\s*Swift\.Int\)", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedMethodReturn_EmitsConformanceWithStub()
    {
        // Protocol with a Self-typed (τ_0_0) return method and a normal method.
        // Self-typed method gets fatalError() stub; normal method gets vtable dispatch.
        var protocol = CreateSimpleProtocol("KFOptionSetter");
        // Self-returning method: func setOption() -> Self (τ_0_0)
        var selfMethod = CreateMethodDecl("setOption");
        selfMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocol.Methods.Add(selfMethod);
        // Normal method
        protocol.Methods.Add(CreateMethodDecl("reset"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.KFOptionSetter", output);
        // Self-typed method gets stub with EveryProtocol substitution
        Assert.Contains("-> EveryProtocol", output);
        Assert.Contains("Self-typed method 'setOption'", output);
        // Normal method gets vtable dispatch
        Assert.Contains("func_reset_1", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedProperty_EmitsConformanceWithStub()
    {
        // Protocol with a Self-typed property and a normal property
        var protocol = CreateSimpleProtocol("ContentEquatable");
        // Self-typed property: var copy: Self (τ_0_0)
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "copy",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("copy_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Normal property
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.ContentEquatable", output);
        // Self-typed property gets stub with EveryProtocol type
        Assert.Contains("public var copy: EveryProtocol", output);
        Assert.Contains("Self-typed property 'copy'", output);
        // Normal property gets vtable dispatch
        Assert.Contains("func_name_get", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedMethodParam_EmitsStubWithEveryProtocol()
    {
        // Protocol with a method that takes Self as a parameter
        var protocol = CreateSimpleProtocol("Comparable");
        var method = CreateMethodDecl("compare");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "other",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "other",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Comparable", output);
        Assert.Contains("other: EveryProtocol", output);
        Assert.Contains("Self-typed method 'compare'", output);
    }

    [Fact]
    public void EmitConformance_SelfAssocTypeProperty_EmitsStubInsteadOfVtableDispatch()
    {
        // Regression: TipKit.Tip has associatedtype Action, and a protocol requirement
        // `var actions: Swift.Array<Self.Action> { get }`. The Self.Action reference
        // can't be dispatched through EveryProtocol's vtable (EveryProtocol has no
        // matching associated type), so it must route to the fatalError() stub path
        // rather than emit a vtable-dispatched implementation that fails to compile.
        var protocol = CreateSimpleProtocol("TipLike");
        protocol.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Action"
        });
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "actions",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array")
            {
                GenericParameters = { new NamedTypeSpec("Self.Action") }
            },
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("actions_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        // The extension is still emitted with the PAT typealias.
        Assert.Contains("extension EveryProtocol: TestModule.TipLike", output);
        Assert.Contains("public typealias Action = Any", output);
        // Self.Action routes through the stub path — Swift.Array<Any> is the rendered
        // stub signature. No vtable dispatch (func_actions_get) is emitted because the
        // Self-typed gate triggers on the Self.Action associated-type reference.
        Assert.Contains("Swift.Array<Any>", output);
        Assert.DoesNotContain("func_actions_get!", output);
    }

    [Fact]
    public void EmitVtableStruct_SkipsSelfAssocTypeProperty()
    {
        // Self.Action references must not generate vtable fields — the C# side has
        // no way to produce a value for the associated type, and the Swift side can't
        // cast a raw pointer to Self.Action in EveryProtocol's context.
        var protocol = CreateSimpleProtocol("TipLike");
        protocol.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Action"
        });
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "actions",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array")
            {
                GenericParameters = { new NamedTypeSpec("Self.Action") }
            },
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("actions_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitVtableStruct(protocol);

        Assert.DoesNotContain("func_actions_get", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedOptionalReturn_EmitsStubWithOptionalEveryProtocol()
    {
        // Protocol with a method returning Optional<Self>: func find() -> Self?
        var protocol = CreateSimpleProtocol("Findable");
        var method = CreateMethodDecl("find");
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional")
            {
                GenericParameters = { new NamedTypeSpec("τ_0_0") }
            },
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Findable", output);
        Assert.Contains("Swift.Optional<EveryProtocol>", output);
    }

    [Fact]
    public void EmitVtableStruct_SkipsSelfTypedAndMethodLevelGenericFields()
    {
        // Protocol with Self-typed members only (no method-level generics → NOT mixed-generic).
        // Self-typed members should not get vtable fields; normal members should.
        var protocol = CreateSimpleProtocol("SelfTypedProtocol");
        // Normal property — should have vtable field
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("count_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Self-typed property — should NOT have vtable field
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "self_prop",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("self_prop_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Normal method — should have vtable field
        protocol.Methods.Add(CreateMethodDecl("normalOp"));

        var output = EmitVtableStruct(protocol);

        // Normal property and method get vtable fields
        Assert.Contains("func_count_get", output);
        Assert.Contains("func_normalOp_0", output);
        // Self-typed property does NOT get vtable field
        Assert.DoesNotContain("func_self_prop", output);
    }

    [Fact]
    public void EmitVtableStruct_MixedGenericProtocol_SkipsAllFields()
    {
        // Mixed-generic protocol (has both method-level generic and non-generic members).
        // ALL members get stubs — vtable has only csVTHandle, no member fields.
        var protocol = CreateSimpleProtocol("MixedGenericProtocol");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("count_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("genericOp"));
        protocol.Methods.Add(CreateMethodDecl("normalOp"));

        var output = EmitVtableStruct(protocol);

        // Only csVTHandle — no member vtable fields
        Assert.Contains("csVTHandle", output);
        Assert.DoesNotContain("func_count_get", output);
        Assert.DoesNotContain("func_normalOp", output);
    }

    #endregion

    #region Helper Methods

    private string EmitEveryProtocolClass()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitEveryProtocolClass(writer);
        return stringWriter.ToString();
    }

    private string EmitVtableStruct(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolVtableStruct(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitProtocolExtension(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolExtension(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitSetVtableFunction(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitSetVtableFunction(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl, HashSet<string> globalSignatures)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl, globalSignatures);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl, HashSet<string> globalSignatures, HashSet<string> nonThrowingOverrides)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl, globalSignatures, nonThrowingOverrides);
        return stringWriter.ToString();
    }

    private string EmitWitnessTableGetter(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitWitnessTableGetter(writer, protocolDecl);
        return stringWriter.ToString();
    }

    #region EC-3: Class-Bound and CaseIterable Gate Tests

    [Fact]
    public void IsClassBoundProtocol_WithIsClassBound_ReturnsFalse()
    {
        // Pure AnyObject class-bound protocols are allowed — EveryProtocol is a class
        // and satisfies the AnyObject constraint. Only NSObjectProtocol is blocked.
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.IsClassBound = true;
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithNSObjectProtocolInheritance_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithAnyObjectInheritance_ReturnsFalse()
    {
        // AnyObject inheritance is allowed — EveryProtocol is a class.
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.AnyObject"));
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_NormalProtocol_ReturnsFalse()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void EmitProtocolConformance_ClassBoundProtocol_EmitsConformance()
    {
        // Class-bound (AnyObject) protocols now emit conformances —
        // EveryProtocol is a class and satisfies AnyObject.
        var protocol = CreateProtocolWithMethod("ClassBoundProto", "doSomething");
        protocol.IsClassBound = true;

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.Contains("EveryProtocol", output);
        Assert.Contains("vtable", output);
    }

    [Fact]
    public void EmitProtocolConformance_NSObjectProtocolInheritor_SkipsEmission()
    {
        var protocol = CreateProtocolWithMethod("STPFormEncodable", "encode");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_CaseIterable_SkipsEmission()
    {
        var protocol = CreateProtocolWithMethod("NVActivityIndicatorType", "allCases");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.CaseIterable"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("EveryProtocol", output);
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNSObjectProtocol_ReturnsTrue()
    {
        // BaseProto inherits NSObjectProtocol; DerivedProto inherits BaseProto.
        // DerivedProto should be detected as class-bound transitively.
        var baseProto = CreateSimpleProtocol("BaseProto");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var derivedProto = CreateSimpleProtocol("DerivedProto");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.BaseProto"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveAnyObject_ReturnsFalse()
    {
        // Transitive AnyObject class-binding is NOT a blocker.
        // EveryProtocol is a class and satisfies AnyObject at any depth.
        var baseProto = CreateSimpleProtocol("Connectable");
        baseProto.IsClassBound = true;

        var derivedProto = CreateSimpleProtocol("StreamConnectable");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Connectable"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNonClassBound_ReturnsFalse()
    {
        var baseProto = CreateSimpleProtocol("Processable");

        var derivedProto = CreateSimpleProtocol("FastProcessable");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Processable"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNSObjectProtocol_StillBlocked()
    {
        // Even though AnyObject is now allowed, NSObjectProtocol is still blocked.
        // Transitive NSObjectProtocol inheritance should return true.
        var baseProto = CreateSimpleProtocol("ObjCDelegate");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var derivedProto = CreateSimpleProtocol("AppDelegate");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.ObjCDelegate"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void WitnessTableGetter_ClassBoundProtocol_UsesDynamicOffset()
    {
        // Class-bound protocols have a 2-word existential container,
        // so the witness table offset should be computed dynamically using
        // MemoryLayout<any Protocol>.size - MemoryLayout<Int>.size
        var protocol = CreateProtocolWithMethod("ClassBoundDelegate", "didReceive");
        protocol.IsClassBound = true;

        var output = EmitWitnessTableGetter(protocol);

        Assert.Contains("MemoryLayout<any", output);
        Assert.Contains(">.size - MemoryLayout<Int>.size", output);
        // Should NOT use hardcoded 4 * MemoryLayout<Int>.size
        Assert.DoesNotContain("4 * MemoryLayout<Int>.size", output);
    }

    [Fact]
    public void WitnessTableGetter_NonClassBoundProtocol_UsesDynamicOffset()
    {
        // Non-class-bound protocols should also use the dynamic formula
        var protocol = CreateProtocolWithMethod("ValueDelegate", "process");

        var output = EmitWitnessTableGetter(protocol);

        Assert.Contains("MemoryLayout<any", output);
        Assert.Contains(">.size - MemoryLayout<Int>.size", output);
    }

    [Fact]
    public void InheritsCaseIterable_Transitive_ReturnsTrue()
    {
        var baseProto = CreateSimpleProtocol("EnumType");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("Swift.CaseIterable"));

        var derivedProto = CreateSimpleProtocol("SpecificEnumType");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.EnumType"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.InheritsCaseIterable(derivedProto, allProtocols));
    }

    [Fact]
    public void InheritsCaseIterable_NoCaseIterable_ReturnsFalse()
    {
        var proto = CreateSimpleProtocol("RegularProto");

        Assert.False(EveryProtocolEmitter.InheritsCaseIterable(proto));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_DirectInheritance_ReturnsTrue()
    {
        // ParentProto has an associated type; ChildProto inherits it via GenericSignature.
        var parentProto = CreateSimpleProtocol("DataSerializer");
        parentProto.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "SerializedObject" });

        var childProto = CreateSimpleProtocol("ResponseSerializer");
        childProto.GenericSignature = "<Self : TestModule.DataSerializer>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_NoAssociatedTypes_ReturnsFalse()
    {
        var parentProto = CreateSimpleProtocol("Describable");
        var childProto = CreateSimpleProtocol("ExtendedDescribable");
        childProto.GenericSignature = "<Self : TestModule.Describable>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.False(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_TransitiveInheritance_ReturnsTrue()
    {
        // Root has associated type, Middle inherits Root, Child inherits Middle.
        var rootProto = CreateSimpleProtocol("Cursor");
        rootProto.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var middleProto = CreateSimpleProtocol("BidirectionalCursor");
        middleProto.GenericSignature = "<Self : TestModule.Cursor>";

        var childProto = CreateSimpleProtocol("RandomAccessCursor");
        childProto.GenericSignature = "<Self : TestModule.BidirectionalCursor>";

        var allProtocols = new List<ProtocolDecl> { rootProto, middleProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_SelfRequirement_ReturnsTrue()
    {
        var parentProto = CreateSimpleProtocol("Comparable");
        parentProto.HasSelfRequirement = true;

        var childProto = CreateSimpleProtocol("Sortable");
        childProto.GenericSignature = "<Self : TestModule.Comparable>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    #endregion

    private static ProtocolDecl CreateSimpleProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private ProtocolDecl CreateProtocolWithProperty(string name, string propertyName, bool hasGetter, bool hasSetter)
    {
        var protocol = CreateSimpleProtocol(name);

        var getterMethod = CreateMethodDecl($"{propertyName}_get");
        var setterMethod = CreateMethodDecl($"{propertyName}_set");

        var accessors = new List<AccessorDecl>();
        if (hasGetter)
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = setterMethod });

        protocol.Properties.Add(new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        });

        return protocol;
    }

    private ProtocolDecl CreateProtocolWithMethod(string name, string methodName)
    {
        var protocol = CreateSimpleProtocol(name);

        protocol.Methods.Add(CreateMethodDecl(methodName));

        return protocol;
    }

    private static MethodDecl CreateMethodDecl(string name, MethodType methodType = MethodType.Instance, bool isConstructor = false, bool throws = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Creates a method with a method-level generic parameter (τ_1_0) in its signature.
    /// This represents methods like resolve&lt;Service&gt;() where the generic is method-level,
    /// not protocol-level (Self).
    /// </summary>
    private static MethodDecl CreateMethodWithMethodLevelGeneric(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type referencing method-level generic param
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Parameter using method-level generic
                new ArgumentDecl
                {
                    Name = "serviceType",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    PrivateName = "serviceType",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "Service", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Creates a method with method-level generics where the parameter uses the given TypeSpec.
    /// </summary>
    private static MethodDecl CreateMethodWithSignature(string name, TypeSpec paramType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = "body",
                    SwiftTypeSpec = paramType,
                    PrivateName = "body",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Creates a method with method-level generics with both param and return TypeSpec.
    /// </summary>
    private static MethodDecl CreateMethodWithSignatureAndReturn(string name, TypeSpec paramType, TypeSpec returnType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = returnType,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = "value",
                    SwiftTypeSpec = paramType,
                    PrivateName = "value",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region Codable Stub Tests

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsDecodable_EmitsDecodableStub()
    {
        var protocol = CreateProtocolWithInheritance("MyDecodableProto", "Swift.Decodable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Decodable", output);
        Assert.Contains("init(from decoder: Decoder)", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsEncodable_EmitsEncodableStub()
    {
        var protocol = CreateProtocolWithInheritance("MyEncodableProto", "Swift.Encodable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Encodable", output);
        Assert.Contains("encode(to encoder: Encoder)", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsCodable_EmitsBothStubs()
    {
        var protocol = CreateProtocolWithInheritance("MyCodableProto", "Swift.Codable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Decodable", output);
        Assert.Contains("extension EveryProtocol: Encodable", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsError_EmitsErrorStub()
    {
        var protocol = CreateProtocolWithInheritance("MyErrorProto", "Swift.Error");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Swift.Error", output);
    }

    [Fact]
    public void EmitCodableStubs_NoInheritance_EmitsNothing()
    {
        var protocol = CreateProtocolWithInheritance("PlainProto");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.DoesNotContain("Decodable", output);
        Assert.DoesNotContain("Encodable", output);
        Assert.DoesNotContain("Error", output);
    }

    [Fact]
    public void EmitCodableStubs_TransitiveDecodable_EmitsDecodableStub()
    {
        // ChildProto inherits ParentProto which inherits Decodable
        var parentProto = CreateProtocolWithInheritance("ParentProto", "Swift.Decodable");
        var childProto = CreateProtocolWithInheritance("ChildProto", "TestModule.ParentProto");

        var allProtocols = new[] { parentProto, childProto };
        var output = EmitCodableStubs(new[] { childProto }, allProtocols);

        Assert.Contains("extension EveryProtocol: Decodable", output);
    }

    #endregion

    #region Static/Composition Protocol Conformance Tests

    [Fact]
    public void EmitProtocolConformance_StaticOnlyProtocol_EmitsStubConformance()
    {
        var protocol = CreateStaticOnlyProtocol("SafeEnumDecodable");

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.SafeEnumDecodable", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void EmitProtocolConformance_CompositionProtocol_EmitsEmptyConformance()
    {
        // Composition protocol: inherits from non-trivial protocols but has no own members
        var protocol = CreateCompositionProtocol("SafeEnumCodable",
            "TestModule.SafeEnumDecodable", "Swift.Encodable");

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.SafeEnumCodable", output);
    }

    #endregion

    #region GenericSig Constraint Gate Tests

    [Fact]
    public void EmitProtocolConformance_ObjCModuleConstraint_SkipsConformance()
    {
        // Protocol with genericSig "<τ_0_0 : UIKit.UICollectionViewDataSource>"
        // should be skipped because UIKit is an ObjC module
        var protocol = CreateProtocolWithMethod("SkeletonDataSource", "numSections");
        protocol.GenericSignature = "<τ_0_0 : UIKit.UICollectionViewDataSource>";

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_UnderscorePrefixedExternalConstraint_SkipsConformance()
    {
        // Protocol with constraint to underscore-prefixed protocol from external module
        // (e.g., StripeApplePay._stpinternal_STPApplePayContextDelegateBase)
        var protocol = CreateProtocolWithMethod("MyDelegate", "didComplete");
        protocol.GenericSignature = "<τ_0_0 : ExternalModule._internalBase>";

        // Module name for emitter is "TestModule", so "ExternalModule" is external
        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_SameModuleSkippedConstraint_SkipsConformance()
    {
        // If ParentProtocol is skipped (has static method requirements)
        // then ChildProtocol with genericSig "<τ_0_0 : TestModule.ParentProtocol>" should also be skipped
        var parentProtocol = CreateSimpleProtocol("ParentProtocol");
        parentProtocol.Methods.Add(CreateMethodDecl("doStuff"));
        parentProtocol.Methods.Add(CreateStaticMethodDecl("staticReq")); // causes skip

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.ParentProtocol>";

        var globalSignatures = new HashSet<string>();

        // First, emit parent — it will be skipped due to static method requirements
        var parentOutput = EmitFullConformance(parentProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", parentOutput);

        // Now emit child — should be skipped because parent was skipped
        var childOutput = EmitFullConformance(childProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", childOutput);
    }

    [Fact]
    public void EmitProtocolConformance_TrivialConstraints_DoesNotSkip()
    {
        // Protocol with only trivial constraints (Sendable, Copyable) should not be skipped
        var protocol = CreateProtocolWithMethod("SimpleProto", "doSomething");
        protocol.GenericSignature = "<τ_0_0 : Swift.Sendable>";

        var output = EmitConformance(protocol);
        Assert.Contains("extension EveryProtocol: TestModule.SimpleProto", output);
    }

    [Fact]
    public void PreScan_ChildBeforeParent_StillSkipsChild()
    {
        // If ChildProtocol (with genericSig referencing ParentProtocol) appears BEFORE
        // ParentProtocol in the list, PreScanProtocols should still detect the dependency.
        var parentProtocol = CreateSimpleProtocol("ParentProtocol");
        parentProtocol.Methods.Add(CreateMethodDecl("doStuff"));
        parentProtocol.Methods.Add(CreateStaticMethodDecl("staticReq")); // causes skip

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.ParentProtocol>";

        // Child appears BEFORE parent in the list (reverse order)
        var protocols = new List<ProtocolDecl> { childProtocol, parentProtocol };

        // Create a fresh emitter to test PreScan
        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(protocols);

        var globalSignatures = new HashSet<string>();

        // Child should be skipped even though it appeared first
        var childOutput = new StringWriter();
        var childWriter = new SwiftWriter(childOutput);
        emitter.EmitProtocolConformance(childWriter, childProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", childOutput.ToString());
    }

    #endregion

    #region Method Type Conflict Gate Tests

    [Fact]
    public void EmitProtocolConformance_SameLabelDifferentTypes_EmitsBothAsOverloads()
    {
        // Two protocols with same method label but different parameter types
        // Both should emit because Swift supports method overloading by parameter type.
        // EveryProtocol can implement both register(delegate: FirstDelegate) and
        // register(delegate: SecondDelegate) as distinct overloads.
        var firstProtocol = CreateSimpleProtocol("FirstHandler");
        firstProtocol.Methods.Add(CreateMethodDeclWithParam("register", "delegate", "FirstDelegate"));
        firstProtocol.Methods.Add(CreateMethodDeclWithParam("parse", "data", "Foundation.Data"));

        var secondProtocol = CreateSimpleProtocol("SecondHandler");
        secondProtocol.Methods.Add(CreateMethodDeclWithParam("register", "delegate", "SecondDelegate"));
        secondProtocol.Methods.Add(CreateMethodDecl("createResponse"));

        var globalSignatures = new HashSet<string>();

        // First protocol emits normally
        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.FirstHandler", firstOutput);
        Assert.Contains("public func register", firstOutput);

        // Second protocol also emits — different param types are valid overloads
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.SecondHandler", secondOutput);
        Assert.Contains("public func register", secondOutput);
        Assert.Contains("public func createResponse", secondOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameParamsDifferentReturn_BothEmitted()
    {
        // Two protocols with same method name and parameter types but different return types.
        // Both are intentionally emitted — the resulting invalid Swift is handled by the
        // wrapper strip/retry mechanism (strips the duplicate function, then the unsatisfiable
        // conformance on retry). Using call-signature dedup to skip the second would leave an
        // empty conformance that the strip script can't recover from.
        var firstProtocol = CreateSimpleProtocol("IntValueProvider");
        var intMethod = CreateMethodDecl("value");
        intMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "", SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            PrivateName = "", IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        firstProtocol.Methods.Add(intMethod);

        var secondProtocol = CreateSimpleProtocol("StringValueProvider");
        var stringMethod = CreateMethodDecl("value");
        stringMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "", SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            PrivateName = "", IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        secondProtocol.Methods.Add(stringMethod);

        var globalSignatures = new HashSet<string>();

        // First protocol emits value() -> Int
        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        Assert.Contains("public func value", firstOutput);

        // Second protocol also emits value() -> String (different full signature).
        // This produces invalid Swift that the strip/retry mechanism will resolve.
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("public func value", secondOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameSignatureNoConflict_EmitsConformance()
    {
        // Two protocols with same method (same name, same types) — no conflict
        var firstProtocol = CreateProtocolWithMethod("ProtoA", "update");
        var secondProtocol = CreateSimpleProtocol("ProtoB");
        secondProtocol.Methods.Add(CreateMethodDecl("update"));
        secondProtocol.Methods.Add(CreateMethodDecl("finish"));

        var globalSignatures = new HashSet<string>();
        EmitFullConformance(firstProtocol, globalSignatures);

        // Second protocol should still emit (update is satisfied by first, finish is new)
        var output = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.ProtoB", output);
        Assert.Contains("public func finish", output);
    }

    [Fact]
    public void EmitProtocolConformance_OverloadedMethodsSameNameDifferentLabels_EmitsBothMethods()
    {
        // Protocol has two methods with the same base name and same parameter types
        // but different argument labels (e.g. pageViewController(_:viewControllerBefore:)
        // and pageViewController(_:viewControllerAfter:)). Both must be emitted.
        var protocol = CreateSimpleProtocol("PageDataSource");
        protocol.Methods.Add(CreateMethodDeclWithParam("pageVC", "before", "UIKit.UIViewController"));
        protocol.Methods.Add(CreateMethodDeclWithParam("pageVC", "after", "UIKit.UIViewController"));

        var vtableOutput = EmitVtableStruct(protocol);
        var extensionOutput = EmitConformance(protocol);

        // Vtable must have TWO distinct entries
        Assert.Contains("func_pageVC_0", vtableOutput);
        Assert.Contains("func_pageVC_1", vtableOutput);

        // Extension must implement BOTH overloads
        Assert.Contains("func pageVC(before", extensionOutput);
        Assert.Contains("func pageVC(after", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameLabelDifferentTypes_ThrowsPreservedPerOverload()
    {
        // Non-throwing validate(input: String) from one protocol must NOT suppress
        // throws on validate(input: Int32) throws from a different protocol.
        // These are distinct Swift overloads; the non-throwing override should only
        // apply when the full signature (name + types) matches.
        var nonThrowingProto = CreateSimpleProtocol("StringValidator");
        nonThrowingProto.Methods.Add(CreateMethodDeclWithParam("validate", "input", "Swift.String"));

        var throwingProto = CreateSimpleProtocol("IntValidator");
        var throwingMethod = CreateMethodDeclWithParam("validate", "input", "Swift.Int32");
        throwingMethod.Throws = true;
        throwingProto.Methods.Add(throwingMethod);

        var globalSignatures = new HashSet<string>();

        // First protocol: non-throwing validate(input: String)
        var firstOutput = EmitFullConformance(nonThrowingProto, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.StringValidator", firstOutput);
        Assert.Contains("public func validate", firstOutput);
        Assert.DoesNotContain("throws", firstOutput);

        // Second protocol: throwing validate(input: Int32)
        // Must retain "throws" — different parameter type means different overload
        var secondOutput = EmitFullConformance(throwingProto, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.IntValidator", secondOutput);
        Assert.Contains("throws", secondOutput);
    }

    #endregion

    #region Method Type Conflict Helper Methods

    private static MethodDecl CreateMethodDeclWithParam(string name, string label, string typeName)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (void)
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Parameter
                new ArgumentDecl
                {
                    Name = label,
                    SwiftTypeSpec = new NamedTypeSpec(typeName),
                    PrivateName = label,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateStaticMethodDecl(string name)
    {
        return CreateMethodDecl(name, MethodType.Static);
    }

    #endregion

    #region Codable/Composition Helper Methods

    private static ProtocolDecl CreateProtocolWithInheritance(string name, params string[] inheritedNames)
    {
        var inherited = inheritedNames.Select(n => new NamedTypeSpec(n)).ToList();
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "value",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateStaticOnlyProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "unparsable",
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    HasStorage = false,
                    IsStatic = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateCompositionProtocol(string name, params string[] inheritedNames)
    {
        var inherited = inheritedNames.Select(n => new NamedTypeSpec(n)).ToList();
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private string EmitCodableStubs(IReadOnlyList<ProtocolDecl> suitableProtocols,
        IReadOnlyList<ProtocolDecl> allProtocols)
    {
        var output = new System.IO.StringWriter();
        var writer = new SwiftWriter(output);
        _emitter.EmitCodableStubsIfNeeded(writer, suitableProtocols, allProtocols, _typeDatabase);
        return output.ToString();
    }

    private string EmitConformance(ProtocolDecl protocol)
    {
        var output = new System.IO.StringWriter();
        var writer = new SwiftWriter(output);
        _emitter.EmitProtocolConformance(writer, protocol);
        return output.ToString();
    }

    #endregion

    #region Closure Method Stub Tests

    [Fact]
    public void EmitProtocolExtension_ClosureMethod_EmitsFatalErrorStub()
    {
        var protocol = CreateSimpleProtocol("EventDelegate");
        // Non-closure method: should have vtable dispatch
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        // Closure method: should get fatalError() stub
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitProtocolExtension(protocol);

        // Closure method gets fatalError stub with exactly one @escaping
        Assert.Contains("public func onComplete(", output);
        Assert.Contains("@escaping", output);
        Assert.DoesNotContain("@escaping @escaping", output);
        Assert.Contains("fatalError", output);
        Assert.Contains("closure method 'onComplete' cannot be dispatched", output);
        // Non-closure method gets real vtable dispatch
        Assert.Contains("public func didReceiveEvent(", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_ClosureMethod_SkipsVtableField()
    {
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitVtableStruct(protocol);

        // Non-closure method gets vtable field
        Assert.Contains("func_didReceiveEvent_0", output);
        // Closure method does NOT get vtable field
        Assert.DoesNotContain("func_onComplete_1", output);
    }

    [Fact]
    public void EmitProtocolConformance_ProtocolWithClosureAndNonClosure_EmitsConformance()
    {
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitFullConformance(protocol);

        // Conformance IS emitted (not skipped entirely)
        Assert.Contains("extension EveryProtocol: TestModule.EventDelegate", output);
        // Witness table getter IS emitted
        Assert.Contains("Get_EveryProtocol_EventDelegate_WitnessTable", output);
    }

    private static MethodDecl CreateMethodWithClosureParam(string name, string paramLabel)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (void)
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Closure parameter
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = CreateEscapingClosure(),
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    [Fact]
    public void EmitProtocolExtension_OptionalClosureParam_NoEscapingAnnotation()
    {
        var protocol = CreateSimpleProtocol("Notifier");
        protocol.Methods.Add(CreateMethodWithOptionalClosureParam("notify", "handler"));

        var output = EmitProtocolExtension(protocol);

        // Optional closures are always escaping in Swift — @escaping on Optional<Closure> is invalid syntax
        Assert.Contains("public func notify(", output);
        Assert.Contains("fatalError", output);
        Assert.DoesNotContain("@escaping", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncClosureMethod_IncludesAsync()
    {
        var protocol = CreateSimpleProtocol("AsyncDelegate");
        protocol.Methods.Add(CreateAsyncMethodWithClosureParam("onComplete", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func onComplete(", output);
        Assert.Contains(" async", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void EmitProtocolExtension_GenericClosureMethod_IncludesGenericClause()
    {
        var protocol = CreateSimpleProtocol("GenericDelegate");
        protocol.Methods.Add(CreateGenericMethodWithClosureParam("transform", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func transform<_G0>(", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncGenericMethod_IncludesAsync()
    {
        // Non-closure method with method-level generics + async — tests EmitMethodLevelGenericStub
        var protocol = CreateSimpleProtocol("AsyncResolver");
        var method = CreateMethodWithMethodLevelGeneric("resolve");
        method.IsAsync = true;
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        Assert.Contains(" async", output);
        Assert.Contains("_G0", output);
    }

    [Fact]
    public void EmitProtocolExtension_ClosureMethodWithMetatype_RewritesGenericParam()
    {
        // Closure method with a generic metatype param: func register<T>(_ type: T.Type, handler: () -> T)
        // Tests that the closure stub's RenderTypeSpec rewrites τ_1_0.Type → _G0.Type
        var protocol = CreateSimpleProtocol("Registry");
        var method = CreateMethodWithClosureParam("register", "handler");
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Add a metatype parameter before the closure
        method.CSSignature.Insert(1, new ArgumentDecl
        {
            Name = "type",
            SwiftTypeSpec = new NamedTypeSpec("τ_1_0.Type"),
            PrivateName = "type",
            IsInOut = false,
            IsGeneric = true,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should rewrite τ_1_0.Type → _G0.Type
        Assert.Contains("_G0.Type", output);
        Assert.DoesNotContain("τ_1_0", output);
    }

    private static MethodDecl CreateMethodWithOptionalClosureParam(string name, string paramLabel)
    {
        var closureSpec = CreateEscapingClosure();
        var optionalSpec = new NamedTypeSpec("Swift.Optional", closureSpec);

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = optionalSpec,
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateAsyncMethodWithClosureParam(string name, string paramLabel)
    {
        var method = CreateMethodWithClosureParam(name, paramLabel);
        method.IsAsync = true;
        return method;
    }

    private static MethodDecl CreateGenericMethodWithClosureParam(string name, string paramLabel)
    {
        var method = CreateMethodWithClosureParam(name, paramLabel);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        return method;
    }

    private static ClosureTypeSpec CreateEscapingClosure()
    {
        var closure = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty
        };
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return closure;
    }

    #endregion

    #region Missing Requirements / Convention-C Skip Tests

    [Fact]
    public void EmitProtocolConformance_HasMissingRequirements_SkipsConformance()
    {
        // Protocol with a requirement that failed ABI parsing (e.g., `some` parameter
        // causes GenericSignatureParser count mismatch). The conformance should be
        // skipped entirely because the emitter can't generate stubs for unknown requirements.
        var protocol = CreateProtocolWithMethod("RowAdapter", "addingScopes");
        protocol.HasMissingRequirements = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_HasConventionCClosureParameters_SkipsConformance()
    {
        // Protocol with @convention(c) closure parameters. ABI JSON lacks convention
        // info, so the closure stub emits @escaping instead of @convention(c).
        var protocol = CreateProtocolWithMethod("FTS5Tokenizer", "tokenize");
        protocol.HasConventionCClosureParameters = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_NoMissingRequirements_EmitsConformance()
    {
        // Normal protocol with all requirements present — should emit conformance normally.
        var protocol = CreateProtocolWithMethod("ValidProtocol", "doWork");
        protocol.HasMissingRequirements = false;
        protocol.HasConventionCClosureParameters = false;

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.ValidProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_HasMissingRequirements_ReturnsTrue()
    {
        // WillSkipConformance should detect missing requirements during pre-scan
        var protocol = CreateProtocolWithMethod("RowAdapter", "addingScopes");
        protocol.HasMissingRequirements = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        // Verify the protocol was added to skipped set by emitting — should be empty
        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_HasConventionCClosureParameters_ReturnsTrue()
    {
        // WillSkipConformance should detect convention-C protocols during pre-scan
        var protocol = CreateProtocolWithMethod("FTS5Tokenizer", "tokenize");
        protocol.HasConventionCClosureParameters = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    #endregion

    #region Swift Keyword Escaping Tests

    [Theory]
    [InlineData("extension")]
    [InlineData("class")]
    [InlineData("func")]
    [InlineData("var")]
    [InlineData("import")]
    public void EmitMethodImplementation_SwiftKeywordParamName_EscapesWithBackticks(string keyword)
    {
        // A protocol method whose parameter name is a Swift keyword should
        // generate backtick-escaped references in the function body.
        var protocol = CreateSimpleProtocol("FileResolver");
        protocol.Methods.Add(CreateMethodDeclWithParam("resolve", keyword, "Swift.String"));

        var output = EmitConformance(protocol);

        // The function body should use backtick escaping for the value reference
        Assert.Contains($"var {keyword}Copy = `{keyword}`", output);
        // The function signature should use the keyword as a parameter label (valid Swift)
        Assert.Contains($"{keyword}: Swift.String", output);
    }

    [Fact]
    public void EmitMethodImplementation_NonKeywordParamName_NoBackticks()
    {
        var protocol = CreateSimpleProtocol("TestProto");
        protocol.Methods.Add(CreateMethodDeclWithParam("process", "fileName", "Swift.String"));

        var output = EmitConformance(protocol);

        Assert.Contains("var fileNameCopy = fileName", output);
        Assert.DoesNotContain("`fileName`", output);
    }

    #endregion

    #region Unsatisfied Stdlib Protocol Gate Tests

    [Theory]
    [InlineData("Hashable")]
    [InlineData("Equatable")]
    [InlineData("Comparable")]
    public void InheritsUnsatisfiedStdlibProtocol_DirectInheritance_ReturnsTrue(string protocolName)
    {
        var protocol = CreateProtocolWithInheritance("MyProto", $"Swift.{protocolName}");

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_IndirectHashableViaCustomProtocol_ReturnsTrue()
    {
        // Protocol chain: RichTextInsertable -> Swift.Hashable (via allProtocols lookup)
        var hashableProto = CreateSwiftStdlibProtocol("Hashable");
        var customProto = CreateProtocolWithInheritance("RichTextInsertable", "Swift.Hashable");

        var allProtocols = new List<ProtocolDecl> { hashableProto, customProto };

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(customProto, allProtocols));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_NoUnsatisfiedInheritance_ReturnsFalse()
    {
        var protocol = CreateProtocolWithInheritance("MyProto", "Swift.Sendable");

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Theory]
    [InlineData("Hashable")]
    [InlineData("Equatable")]
    [InlineData("Comparable")]
    public void InheritsUnsatisfiedStdlibProtocol_LibraryDefinedSameName_ReturnsFalse(string protocolName)
    {
        // A library-defined protocol named "Hashable" (module != Swift) should NOT
        // be treated as an unsatisfied stdlib protocol.
        var protocol = CreateProtocolWithInheritance(protocolName);

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_InheritsLibraryDefinedHashable_ReturnsFalse()
    {
        // A protocol that inherits from a library-local "Hashable" (not Swift.Hashable)
        // should NOT be treated as inheriting an unsatisfied stdlib protocol.
        // The unqualified name "Hashable" in InheritedProtocols must be resolved
        // via allProtocols to check the actual module.
        var libraryHashable = CreateProtocolWithInheritance("Hashable");
        var myProto = CreateProtocolWithInheritance("MyProto", "Hashable");

        var allProtocols = new List<ProtocolDecl> { libraryHashable, myProto };

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(myProto, allProtocols));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_InheritsSwiftQualifiedHashable_ReturnsTrue()
    {
        // A protocol that inherits from "Swift.Hashable" (explicitly qualified)
        // should be correctly identified without needing allProtocols lookup.
        var myProto = CreateProtocolWithInheritance("MyProto", "Swift.Hashable");

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(myProto));
    }

    #endregion

    /// <summary>
    /// Creates a protocol declaration as if it's from the Swift standard library.
    /// </summary>
    private static ProtocolDecl CreateSwiftStdlibProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Swift.{name}"),
            MangledName = $"$ss{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }
}
