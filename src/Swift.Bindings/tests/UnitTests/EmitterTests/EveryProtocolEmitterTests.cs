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
    public void EmitProtocolConformance_SkipsProtocolsWithNoImplementableMembers()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        // No properties, no non-static methods, no subscripts

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
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
    public void EmitProtocolConformance_SkipsStaticAndConstructorMethods()
    {
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");
        protocolDecl.Methods.Add(CreateMethodDecl("instanceMethod"));
        protocolDecl.Methods.Add(CreateMethodDecl("utility", methodType: MethodType.Static));
        protocolDecl.Methods.Add(CreateMethodDecl("init", isConstructor: true));

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("public func instanceMethod()", output);
        Assert.DoesNotContain("public func utility", output);
        Assert.DoesNotContain("public func init(", output);
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
    public void EmitProtocolExtension_WithGenericTypeParameter_UsesAnyTypeErasure()
    {
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

        Assert.Contains("public var item: Any", output);
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
        var nonThrowingOverrides = new HashSet<string> { "process()" };
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

        // Property type should NOT have @escaping (invalid on property declarations)
        Assert.DoesNotContain("@escaping", output);
        // Should have the optional closure type
        Assert.Contains("public var onDismiss:", output);
        // Metatype should use Optional<...> syntax for .self access
        Assert.Contains("Optional<", output);
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
    public void IsClassBoundProtocol_WithIsClassBound_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.IsClassBound = true;
        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithNSObjectProtocolInheritance_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithAnyObjectInheritance_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.AnyObject"));
        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_NormalProtocol_ReturnsFalse()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void EmitProtocolConformance_ClassBoundProtocol_SkipsEmission()
    {
        var protocol = CreateProtocolWithMethod("ClassBoundProto", "doSomething");
        protocol.IsClassBound = true;

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("EveryProtocol", output);
        Assert.DoesNotContain("vtable", output);
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
    public void IsClassBoundProtocol_TransitiveAnyObject_ReturnsTrue()
    {
        var baseProto = CreateSimpleProtocol("Connectable");
        baseProto.IsClassBound = true;

        var derivedProto = CreateSimpleProtocol("StreamConnectable");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Connectable"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
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

    #endregion
}
