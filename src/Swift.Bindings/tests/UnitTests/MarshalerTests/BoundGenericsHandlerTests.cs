// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the BoundGenericsHandler class, focusing on existential types
/// as generic type arguments (e.g., Dictionary&lt;String, Any&gt;).
/// </summary>
public class BoundGenericsHandlerTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly BoundGenericsHandler _handler;

    public BoundGenericsHandlerTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _handler = new BoundGenericsHandler(_typeDatabase);
    }

    #region Existential Type Arguments Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithAny_ResolvesToExistentialContainer0()
    {
        // Swift: Dictionary<String, Any>
        // Bare Any (0 effective protocols) is now supported in containers via ExistentialContainer0.
        var anyTypeSpec = new ProtocolListTypeSpec(); // Empty protocol list = Any
        var keyTypeSpec = new NamedTypeSpec("Swift.String");
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(keyTypeSpec);
        dictTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(dictTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftDictionary", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithAnySendable_ResolvesToExistentialContainer0()
    {
        // Swift: Dictionary<String, any Sendable> — the cross-library Nuke userInfo shape.
        // `any Sendable` is a marker-only existential: it filters to zero non-marker protocols,
        // so it is ABI-identical to bare Any and must resolve to ExistentialContainer0 (Box/Unbox),
        // NOT the dropped AnyType fallback.
        var anySendable = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(anySendable);

        var argDecl = CreateArgumentDecl(dictTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftDictionary", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayOfAnySendable_ResolvesToExistentialContainer0()
    {
        // Swift: Array<any Sendable> — marker-only element shares the bare-Any Container0 ABI.
        var anySendable = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anySendable);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_SimdAlias_CollapsesViaSharedService()
    {
        // Swift.SIMD3<Swift.Float> routes through the shared
        // BoundGenericTranslation.TryResolveSimdAliasCSharp short-circuit and collapses to the
        // non-generic alias record rather than the invalid `simd_float3<float>`.
        var simd3 = new NamedTypeSpec("Swift.SIMD3");
        simd3.GenericParameters.Add(new NamedTypeSpec("Swift.Float"));

        var argDecl = CreateArgumentDecl(simd3);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Equal("System.Numerics.Vector3", result);
        Assert.DoesNotContain("<", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayWithAny_ResolvesToExistentialContainer0()
    {
        // Swift: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalWithAny_ResolvesToExistentialContainer0()
    {
        // Swift: Optional<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(optionalTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftOptional", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_WithSingleProtocolExistential_FallsBackToAnyType()
    {
        // Swift: Array<any Equatable>
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(protocolList);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_WithTwoProtocolExistential_FallsBackToAnyType()
    {
        // Swift: Array<any Equatable & Hashable>
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(protocolList);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("AnyType", result);
    }

    #endregion

    #region Nested Generic with Existential Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NestedArrayOfDictionaryWithAny_ResolvesToExistentialContainer0()
    {
        // Swift: Array<Dictionary<String, Any>>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var keyTypeSpec = new NamedTypeSpec("Swift.String");
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(keyTypeSpec);
        dictTypeSpec.GenericParameters.Add(anyTypeSpec);

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(dictTypeSpec);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("SwiftDictionary", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    #endregion

    #region Nested Existential Container Admission Gate

    // These pin IsContainerWithSupportedDirectExistential — the admission gate that
    // supports nested existential CONTAINERS. The recursion must admit genuine nested
    // Array/Dictionary leaves but MUST NOT descend into an Optional-wrapped existential element,
    // which lowers to the same Array<Optional<existential>> ABI shape as a variadic
    // ExpressibleByArrayLiteral init whose variadic-lowered shape the @_cdecl wrapper cannot
    // forward. Bare Any keeps these focused on the gate's container/Optional shape decisions
    // (IsValidExistentialForContainer short-circuits on bare Any, so no TypeRecord setup is needed).

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ArrayOfArrayOfAny_Admitted()
    {
        // Array<Array<Any>> — genuine nested container leaf.
        var spec = BuildArrayOf(BuildArrayOf(new ProtocolListTypeSpec()));
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ArrayOfDictionaryOfAny_Admitted()
    {
        // Array<Dictionary<String, Any>> — nested container in the array element.
        var spec = BuildArrayOf(BuildDictOf(new NamedTypeSpec("Swift.String"), new ProtocolListTypeSpec()));
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_DictionaryValueArrayOfAny_Admitted()
    {
        // Dictionary<String, Array<Any>> — nested container in the VALUE slot.
        var spec = BuildDictOf(new NamedTypeSpec("Swift.String"), BuildArrayOf(new ProtocolListTypeSpec()));
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_DictionaryValueDictionaryOfAny_Admitted()
    {
        // Dictionary<String, Dictionary<String, Any>> — nested dictionary container shape.
        var inner = BuildDictOf(new NamedTypeSpec("Swift.String"), new ProtocolListTypeSpec());
        var spec = BuildDictOf(new NamedTypeSpec("Swift.String"), inner);
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ArrayOfOptionalOfAny_NotAdmitted()
    {
        // Array<Optional<Any>> — element is an Optional-wrapped existential, NOT a genuine nested
        // Array/Dictionary leaf. Admitting it would route a variadic-lowered
        // ExpressibleByArrayLiteral init's [any P?] through a normal constructor wrapper and
        // emit an uncompilable @_cdecl call. Regression guard for the gate narrowing.
        var spec = BuildArrayOf(BuildOptionalOf(new ProtocolListTypeSpec()));
        Assert.False(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_DictionaryValueOptionalOfAny_NotAdmitted()
    {
        // Dictionary<String, Optional<Any>> — value is an Optional-wrapped existential, same
        // exclusion as the Array case: not a genuine nested container leaf.
        var spec = BuildDictOf(new NamedTypeSpec("Swift.String"), BuildOptionalOf(new ProtocolListTypeSpec()));
        Assert.False(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_OptionalOuterOfArrayOfAny_StillAdmitted()
    {
        // Optional<Array<Any>> — the OUTER Optional branch is untouched by the gate narrowing; an
        // Optional-wrapped supported container is still admitted. Only the element/value RECURSION
        // is restricted, never the top-level Optional unwrap.
        var spec = BuildOptionalOf(BuildArrayOf(new ProtocolListTypeSpec()));
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_DirectOptionalOfAny_StillAdmitted()
    {
        // Optional<Any> — direct existential in an Optional is a long-standing supported pattern;
        // the gate narrowing does not touch it.
        var spec = BuildOptionalOf(new ProtocolListTypeSpec());
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_DictionaryValueAnySendable_Admitted()
    {
        // Dictionary<String, any Sendable> — marker-only value is zero-witness, so the gate
        // admits it via the same ExistentialContainer0 path as bare Any (no TypeRecord needed:
        // IsValidExistentialForContainer short-circuits on the zero-witness check).
        var anySendable = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var spec = BuildDictOf(new NamedTypeSpec("Swift.String"), anySendable);
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ArrayOfAnySendable_Admitted()
    {
        // Array<any Sendable> — marker-only element admitted via the zero-witness accept.
        var anySendable = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var spec = BuildArrayOf(anySendable);
        Assert.True(_handler.IsContainerWithSupportedDirectExistential(spec));
    }

    private static NamedTypeSpec BuildArrayOf(TypeSpec element)
    {
        var arr = new NamedTypeSpec("Swift.Array");
        arr.GenericParameters.Add(element);
        return arr;
    }

    private static NamedTypeSpec BuildDictOf(TypeSpec key, TypeSpec value)
    {
        var dict = new NamedTypeSpec("Swift.Dictionary");
        dict.GenericParameters.Add(key);
        dict.GenericParameters.Add(value);
        return dict;
    }

    private static NamedTypeSpec BuildOptionalOf(TypeSpec inner)
    {
        var opt = new NamedTypeSpec("Swift.Optional");
        opt.GenericParameters.Add(inner);
        return opt;
    }

    #endregion

    #region IsBoundGeneric Tests

    [Fact]
    public void IsBoundGeneric_WithGenericParameters_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var argDecl = CreateArgumentDecl(typeSpec);
        var result = _handler.IsBoundGeneric(argDecl);

        Assert.True(result);
    }

    [Fact]
    public void IsBoundGeneric_WithoutGenericParameters_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var argDecl = CreateArgumentDecl(typeSpec);

        var result = _handler.IsBoundGeneric(argDecl);

        Assert.False(result);
    }

    [Fact]
    public void IsBoundGeneric_WithProtocolListTypeSpec_ReturnsFalse()
    {
        // ProtocolListTypeSpec is not a NamedTypeSpec, so IsBoundGeneric returns false
        var typeSpec = new ProtocolListTypeSpec();
        var argDecl = CreateArgumentDecl(typeSpec);

        var result = _handler.IsBoundGeneric(argDecl);

        Assert.False(result);
    }

    #endregion

    #region Unsupported Existential Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_WithTooManyProtocols_FallsBackToAnyType()
    {
        // More than 8 protocols should fall back to AnyType
        var protocols = Enumerable.Range(1, 9)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(protocolList);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // When existential is unsupported, falls back to AnyType
        Assert.Contains("SwiftArray", result);
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void TryGetFirstExistentialTypeArgument_NestedGeneric_ReturnsTrueAndType()
    {
        // Swift: Array<Dictionary<String, any Equatable>>
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(protocolList);

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(dictTypeSpec);

        var found = _handler.TryGetFirstExistentialTypeArgument(arrayTypeSpec, out var existentialType);

        Assert.True(found);
        Assert.Equal("Swift.Equatable", existentialType);
    }

    [Fact]
    public void TryGetFirstExistentialTypeArgument_NoExistential_ReturnsFalse()
    {
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var found = _handler.TryGetFirstExistentialTypeArgument(arrayTypeSpec, out var existentialType);

        Assert.False(found);
        Assert.Equal(string.Empty, existentialType);
    }

    [Fact]
    public void TryGetFirstUnsupportedExistentialTypeArgument_SupportedExistential_ReturnsFalse()
    {
        // Swift: Array<any Equatable> — 1 protocol, supported
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(protocolList);

        var found = _handler.TryGetFirstUnsupportedExistentialTypeArgument(arrayTypeSpec, out var existentialType);

        Assert.False(found);
        Assert.Equal(string.Empty, existentialType);
    }

    [Fact]
    public void TryGetFirstUnsupportedExistentialTypeArgument_UnsupportedExistential_ReturnsTrueAndType()
    {
        // 9 protocols — exceeds MaxSupportedWitnessTables (8), unsupported
        var protocols = Enumerable.Range(1, 9)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(protocolList);

        var found = _handler.TryGetFirstUnsupportedExistentialTypeArgument(arrayTypeSpec, out var existentialType);

        Assert.True(found);
        Assert.NotEmpty(existentialType);
    }

    [Fact]
    public void TryGetFirstUnsupportedExistentialTypeArgument_NoExistential_ReturnsFalse()
    {
        // Swift: Array<Int> — no existential at all
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var found = _handler.TryGetFirstUnsupportedExistentialTypeArgument(arrayTypeSpec, out var existentialType);

        Assert.False(found);
        Assert.Equal(string.Empty, existentialType);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_LocalTypeWithoutConformance_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("ValueProviderStorage", moduleDecl, "T", "TestModule.AnyInterpolatable");
        CreateStructDecl("VectorAnimationVector3D", moduleDecl);

        var boundGeneric = new NamedTypeSpec("TestModule.ValueProviderStorage", new NamedTypeSpec("TestModule.VectorAnimationVector3D"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_EquatableConformance_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Box", moduleDecl, "T", "Swift.Equatable");
        CreateStructDecl("Point", moduleDecl, new[] { "Swift.Equatable" });

        var boundGeneric = new NamedTypeSpec("TestModule.Box", new NamedTypeSpec("TestModule.Point"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_GeneralProtocolConformance_ReturnsFalse()
    {
        // A1: SatisfiesConstraint now checks all protocol conformances, not just Equatable.
        // Setup: Container<T> where T: Serializable, and DataModel conforms to Serializable.
        var moduleDecl = CreateModuleDecl("TestModule");
        var serializableProtocol = new ProtocolDecl
        {
            Name = "Serializable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Serializable"),
            MangledName = "$s10TestModule12SerializableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Protocols.Add(serializableProtocol);

        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Serializable");
        CreateStructDecl("DataModel", moduleDecl, new[] { "TestModule.Serializable" });

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("TestModule.DataModel"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_GeneralProtocolConformance_ClassDecl_ReturnsFalse()
    {
        // A1: Verify SatisfiesConstraint works for ClassDecl types with non-Equatable conformances.
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Processor", moduleDecl, "T", "TestModule.ImageProcessor");
        CreateClassDecl("ResizingProcessor", moduleDecl, new[] { "TestModule.ImageProcessor" });

        var boundGeneric = new NamedTypeSpec("TestModule.Processor", new NamedTypeSpec("TestModule.ResizingProcessor"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_GeneralProtocolConformance_NoConformance_ReturnsTrue()
    {
        // A1: Type argument has NO conformance to the constraint protocol — should still fail.
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Serializable");
        CreateStructDecl("RawData", moduleDecl); // No conformance to Serializable

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("TestModule.RawData"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_GeneralProtocolConformance_EnumDecl_ReturnsFalse()
    {
        // A1: Verify SatisfiesConstraint works for EnumDecl types with non-Equatable conformances.
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Wrapper", moduleDecl, "T", "TestModule.Codable");
        CreateEnumDecl("Status", moduleDecl, new[] { "TestModule.Codable" });

        var boundGeneric = new NamedTypeSpec("TestModule.Wrapper", new NamedTypeSpec("TestModule.Status"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ConcreteTypeTransitiveConformance_ReturnsFalse()
    {
        // ConcreteType : ChildProtocol should satisfy
        // T : ParentProtocol when ChildProtocol : ParentProtocol.
        // This tests the concrete-type path (not generic-parameter path).
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create ParentProtocol
        var parentProtocol = new ProtocolDecl
        {
            Name = "ParentProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ParentProtocol"),
            MangledName = "$s10TestModule14ParentProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(parentProtocol);

        // Create ChildProtocol : ParentProtocol
        var childProtocol = new ProtocolDecl
        {
            Name = "ChildProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ChildProtocol"),
            MangledName = "$s10TestModule13ChildProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.ParentProtocol") },
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(childProtocol);

        // Container<T> where T: ParentProtocol
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.ParentProtocol");

        // ConcreteModel conforms to ChildProtocol (NOT directly to ParentProtocol)
        CreateStructDecl("ConcreteModel", moduleDecl, new[] { "TestModule.ChildProtocol" });

        // Container<ConcreteModel> — should satisfy constraint via transitive inheritance
        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("TestModule.ConcreteModel"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ConcreteTypeUnrelatedProtocol_ReturnsTrue()
    {
        // Negative test for transitive conformance: ConcreteType : UnrelatedProtocol
        // does NOT satisfy T : RequiredProtocol.
        var moduleDecl = CreateModuleDecl("TestModule");

        var requiredProtocol = new ProtocolDecl
        {
            Name = "RequiredProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RequiredProtocol"),
            MangledName = "$s10TestModule16RequiredProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(requiredProtocol);

        var unrelatedProtocol = new ProtocolDecl
        {
            Name = "UnrelatedProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UnrelatedProtocol"),
            MangledName = "$s10TestModule17UnrelatedProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(unrelatedProtocol);

        // Container<T> where T: RequiredProtocol
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.RequiredProtocol");

        // Widget conforms to UnrelatedProtocol (NOT RequiredProtocol or child of it)
        CreateStructDecl("Widget", moduleDecl, new[] { "TestModule.UnrelatedProtocol" });

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("TestModule.Widget"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ExternalConcreteType_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("ValueProviderStorage", moduleDecl, "T", "TestModule.AnyInterpolatable");

        var boundGeneric = new NamedTypeSpec(
            "TestModule.ValueProviderStorage",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
        Assert.Contains("Swift.Array<Swift.Double>", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ExternalClassBoundConstraint_TypeDatabaseSubclass_ReturnsFalse()
    {
        // External XML/database-owned subclasses
        // (e.g. Foundation.UnitTemperature satisfying Foundation.Dimension)
        // must satisfy a class-bound generic constraint even though they have
        // no local TypeDecl. Without this path, the typeArgumentDecl == null
        // short-circuit returns false and the consuming member is silently
        // skipped.
        var types = new Dictionary<string, TypeRecord>
        {
            ["Foundation.Dimension"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDimension"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class,
            },
            ["Foundation.UnitTemperature"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUnitTemperature"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.UnitTemperature"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
            },
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var moduleDecl = CreateModuleDecl("TestModule");
        // Measurement<T> where T : Foundation.Dimension. Class-bound, not protocol.
        CreateGenericStructDecl("MeasurementBag", moduleDecl, "T", "Foundation.Dimension");

        var boundGeneric = new NamedTypeSpec("TestModule.MeasurementBag",
            new NamedTypeSpec("Foundation.UnitTemperature"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ExternalClassBoundConstraint_UnrelatedClass_ReturnsTrue()
    {
        // Negative case: a TypeDatabase-known class with no relation to the
        // class-bound constraint must still fail. Confirms the class-walk only
        // accepts genuine subclasses, not arbitrary class kinds.
        var types = new Dictionary<string, TypeRecord>
        {
            ["Foundation.Dimension"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDimension"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
            ["Foundation.UnrelatedClass"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUnrelatedClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.UnrelatedClass"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = null,
            },
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("MeasurementBag", moduleDecl, "T", "Foundation.Dimension");

        var boundGeneric = new NamedTypeSpec("TestModule.MeasurementBag",
            new NamedTypeSpec("Foundation.UnrelatedClass"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_InheritedProtocolConstraint_ReturnsFalse()
    {
        // Setup: Wrapper<U> where U: ChildProtocol, Container<T> where T: ParentProtocol
        // ChildProtocol inherits from ParentProtocol.
        // Container<U> should be valid since ChildProtocol satisfies ParentProtocol.
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create ParentProtocol
        var parentProtocol = new ProtocolDecl
        {
            Name = "ParentProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ParentProtocol"),
            MangledName = "$s10TestModule14ParentProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(parentProtocol);

        // Create ChildProtocol : ParentProtocol
        var childProtocol = new ProtocolDecl
        {
            Name = "ChildProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ChildProtocol"),
            MangledName = "$s10TestModule13ChildProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.ParentProtocol") },
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(childProtocol);

        // Container<T> where T: ParentProtocol
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.ParentProtocol");

        // Wrapper<U> where U: ChildProtocol (the parent type)
        var wrapperDecl = CreateGenericStructDecl("Wrapper", moduleDecl, "U", "TestModule.ChildProtocol");

        // Context: a property on Wrapper<U> with type Container<U>
        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("τ_0_0"));
        var contextDecl = new PropertyDecl
        {
            Name = "storage",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = wrapperDecl, // Parent is Wrapper<U>
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        // Should pass: U is constrained to ChildProtocol which inherits ParentProtocol
        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_UnrelatedProtocolConstraint_ReturnsTrue()
    {
        // Setup: Wrapper<U> where U: UnrelatedProtocol, Container<T> where T: RequiredProtocol
        // UnrelatedProtocol does NOT inherit from RequiredProtocol.
        // Container<U> should fail since UnrelatedProtocol doesn't satisfy RequiredProtocol.
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create RequiredProtocol (no parent)
        var requiredProtocol = new ProtocolDecl
        {
            Name = "RequiredProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RequiredProtocol"),
            MangledName = "$s10TestModule16RequiredProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(requiredProtocol);

        // Create UnrelatedProtocol (does NOT inherit RequiredProtocol)
        var unrelatedProtocol = new ProtocolDecl
        {
            Name = "UnrelatedProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UnrelatedProtocol"),
            MangledName = "$s10TestModule17UnrelatedProtocolMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(unrelatedProtocol);

        // Container<T> where T: RequiredProtocol
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.RequiredProtocol");

        // Wrapper<U> where U: UnrelatedProtocol
        var wrapperDecl = CreateGenericStructDecl("Wrapper", moduleDecl, "U", "TestModule.UnrelatedProtocol");

        // Context: property on Wrapper<U> with type Container<U>
        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("τ_0_0"));
        var contextDecl = new PropertyDecl
        {
            Name = "storage",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = wrapperDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        // Should fail: UnrelatedProtocol doesn't satisfy RequiredProtocol
        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ParentSugaredGenericParam_ReturnsFalse()
    {
        // Issue C (2026-04-22): MusicKit-shape bug — a method on a generic parent type
        // references the parent's own generic param as a bound-generic argument:
        //   extension MusicItemCollection where MusicItemType: MusicItem {
        //       func index(before: Int) -> MusicItemCollection<MusicItemType>.Index
        //   }
        // The type argument 'MusicItemType' arrives at SatisfiesConstraint as a
        // NamedTypeSpec with a multi-character sugared name. TypeSpecHelpers.IsGenericTypeParameter
        // only recognises short names and τ_-prefixed ABI names, so without the parent/method
        // declared-param lookup, SatisfiesConstraint falls through to concrete-type resolution
        // and reports the constraint as unsatisfied — even though the parent explicitly
        // declares `MusicItemType : MusicItem`.
        var moduleDecl = CreateModuleDecl("MusicKit");
        var outerDecl = CreateGenericStructDecl("MusicItemCollection", moduleDecl, "MusicItemType", "MusicKit.MusicItem");

        // Bound-generic reference to MusicItemCollection<MusicItemType> with the parent's
        // own generic parameter (multi-character sugared name) as the argument.
        var boundGeneric = new NamedTypeSpec("MusicKit.MusicItemCollection",
            new NamedTypeSpec("MusicItemType"));
        var contextDecl = new PropertyDecl
        {
            Name = "index",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = outerDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ParentSugaredGenericParam_UnrelatedProtocol_ReturnsTrue()
    {
        // Negative case: even when the type argument references a sugared parent generic
        // param by name, the constraint must still fail if the parent's declared
        // constraints on that parameter don't include (or inherit from) the required
        // protocol. Protects against the fix becoming too permissive.
        var moduleDecl = CreateModuleDecl("MusicKit");
        var outerDecl = CreateGenericStructDecl("MusicItemCollection", moduleDecl, "MusicItemType", "MusicKit.UnrelatedProtocol");
        CreateGenericStructDecl("Slice", moduleDecl, "Element", "MusicKit.MusicItem");

        var boundGeneric = new NamedTypeSpec("MusicKit.Slice",
            new NamedTypeSpec("MusicItemType"));
        var contextDecl = new PropertyDecl
        {
            Name = "slice",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = outerDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_MethodSugaredGenericParam_ReturnsFalse()
    {
        // Issue C companion: a method-level generic parameter uses a multi-character
        // sugared name (e.g. 'SectionType') as a bound-generic argument. Analogous to
        // the parent-generic MusicKit case, but the generic is declared on the method
        // itself rather than the enclosing type:
        //   struct ItemProcessor {
        //       func processBatch<SectionType: Describable>(...) -> Container<SectionType>
        //   }
        // Without the method-level `IsDeclaredGenericParam` check in SatisfiesConstraint,
        // the sugared name falls through to concrete-type resolution (TypeSpecHelpers only
        // recognises τ_-prefixed and short names), FindTypeDecl returns null, and the
        // constraint is falsely reported as unsatisfied.
        var moduleDecl = CreateModuleDecl("TestModule");
        var processorDecl = CreateStructDecl("ItemProcessor", moduleDecl);
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Describable");

        var method = new MethodDecl
        {
            Name = "processBatch",
            ParentDecl = processorDecl,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(
                    TypeName: "τ_0_0",
                    SugaredTypeName: "SectionType",
                    GenericConformances: new List<GenericParameterConformance>
                    {
                        new(
                            Path: new[] { "τ_0_0" },
                            ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                            Kind: ConformanceKind.Protocol)
                    },
                    AssosiatedTypeConformances: new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false
        };

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("SectionType"));

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ScopelessGenericParam_NoEmittableScope_ReturnsTrue()
    {
        // Finding 20 fail-closed flip: a generic-parameter-shaped type argument (τ_0_0) that
        // belongs to NO emittable scope — neither the (non-generic) enclosing type nor any
        // method-level generics — carries no C# `where` clause. Emitting the bound generic
        // would reference an unconstrained type parameter, so the conformance is genuinely
        // unprovable. The member must be dropped (constraint reported unsatisfied).
        //
        // Previously this path returned "satisfied" (fail-open), silently emitting bindings
        // whose constraint nothing actually guaranteed.
        var moduleDecl = CreateModuleDecl("TestModule");
        var host = CreateStructDecl("Host", moduleDecl); // non-generic: no parent generic params
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Describable");

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("τ_0_0"));
        var contextDecl = new PropertyDecl
        {
            Name = "storage",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = host,    // non-generic struct; not a MethodDecl → no method-level generics
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_FreeFunctionMethodLevelParam_ReturnsFalse()
    {
        // Regression guard for the fail-closed flip: a free function (parent is the module,
        // NOT a TypeDecl) declares its own generic parameter carrying the required constraint.
        // parentTypeGenericParams is null, but the function's own generics are threaded as
        // methodGenericParams — C# emits the `where` on the generated method, so the
        // conformance is provably satisfied and the member must be kept.
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Describable");

        var freeFunction = new MethodDecl
        {
            Name = "makeContainer",
            ParentDecl = moduleDecl,    // free function: parent is the module, not a TypeDecl
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(
                    TypeName: "τ_0_0",
                    SugaredTypeName: "Element",
                    GenericConformances: new List<GenericParameterConformance>
                    {
                        new(
                            Path: new[] { "τ_0_0" },
                            ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                            Kind: ConformanceKind.Protocol)
                    },
                    AssosiatedTypeConformances: new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false
        };

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("Element"));

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, freeFunction, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_MethodLevelParamMissingConstraint_ReturnsTrue()
    {
        // A method-level generic parameter exists but does NOT declare the constraint the
        // bound generic requires. C# would emit the method parameter without a matching
        // `where`, so `Container<Element>` (which requires Describable) is unsatisfied. This
        // shape is invalid Swift, but fail-closed handling drops it rather than emitting
        // uncompilable C#.
        var moduleDecl = CreateModuleDecl("TestModule");
        var processorDecl = CreateStructDecl("ItemProcessor", moduleDecl);
        CreateGenericStructDecl("Container", moduleDecl, "T", "TestModule.Describable");

        var method = new MethodDecl
        {
            Name = "process",
            ParentDecl = processorDecl,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(
                    TypeName: "τ_0_0",
                    SugaredTypeName: "Element",
                    // Note: no Describable conformance declared on this method param.
                    GenericConformances: new List<GenericParameterConformance>(),
                    AssosiatedTypeConformances: new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false
        };

        var boundGeneric = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("Element"));

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    #endregion

    #region Mixed Generic Parameter Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_MixedGenericParameters_HandlesCorrectly()
    {
        // Swift: Dictionary<Int, Any>
        var keyTypeSpec = new NamedTypeSpec("Swift.Int");
        var anyTypeSpec = new ProtocolListTypeSpec();
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(keyTypeSpec);
        dictTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(dictTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftDictionary", result);
        Assert.Contains("nint", result); // Int maps to nint (pointer-sized)
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NamedSwiftVoid_MapsToSwiftVoid()
    {
        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Void"));
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        var argDecl = CreateArgumentDecl(resultTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("Swift.SwiftVoid", result);
        Assert.DoesNotContain("<void", result);
    }

    #endregion

    #region Property Bound Generic Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_Property_WithAny_ResolvesToExistentialContainer0()
    {
        // Swift property: var items: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var propertyDecl = CreatePropertyDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(propertyDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("ExistentialContainer0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void IsBoundGeneric_Property_WithGenericParameters_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var propertyDecl = CreatePropertyDecl(typeSpec);
        var result = _handler.IsBoundGeneric(propertyDecl);

        Assert.True(result);
    }

    #endregion

    #region GetBufferType Fallback Tests

    [Fact]
    public void GetBufferType_UnmappedBoundGeneric_ReturnsIntPtr()
    {
        // Bound generic type not in s_bufferTypeMap (e.g., TestModule.CustomGeneric<Int>)
        // G7 fix: fallback returns "IntPtr" instead of AnyType
        var typeSpec = new NamedTypeSpec("TestModule.CustomGeneric");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var argDecl = CreateArgumentDecl(typeSpec);
        var result = _handler.GetBufferType(argDecl);

        Assert.Equal("IntPtr", result);
    }

    [Theory]
    [InlineData("Swift.Dictionary")]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Set")]
    [InlineData("Swift.Optional")]
    [InlineData("Swift.Result")]
    [InlineData("Swift.ClosedRange")]
    public void IsBareGenericUsage_StdlibGenericWithoutArgs_ReturnsTrue(string stdlibName)
    {
        var isBare = _handler.IsBareGenericUsage(new NamedTypeSpec(stdlibName), moduleDecl: null);
        Assert.True(isBare);
    }

    [Fact]
    public void IsBareGenericUsage_ModuleGenericWithoutArgs_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Box", moduleDecl, "T", "Swift.Equatable");

        var isBare = _handler.IsBareGenericUsage(new NamedTypeSpec("TestModule.Box"), moduleDecl);
        Assert.True(isBare);
    }

    [Fact]
    public void IsBareGenericUsage_ModuleGenericWithArgs_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Box", moduleDecl, "T", "Swift.Equatable");

        var bound = new NamedTypeSpec("TestModule.Box");
        bound.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var isBare = _handler.IsBareGenericUsage(bound, moduleDecl);
        Assert.False(isBare);
    }

    [Fact]
    public void HasBareGenericUsage_NestedInsideOptional_ReturnsTrue()
    {
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Swift.Dictionary"));

        Assert.True(_handler.HasBareGenericUsage(optional, moduleDecl: null));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithTupleArgInNonOptional_ReturnsTrue()
    {
        // Emitted generics have 'where T : ISwiftObject' — ValueTuple can't satisfy it
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var generic = new NamedTypeSpec("TestModule.Future");
        generic.GenericParameters.Add(tuple);

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithTupleArgInOptional_ReturnsFalse()
    {
        // SwiftOptional<T> has no ISwiftObject constraint — tuples are valid
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(tuple);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithClosureArg_ReturnsFalse()
    {
        // Closures fall back to object via AnyType/ContainsPlaceholder — not blocked
        var closure = new ClosureTypeSpec(
            new TupleTypeSpec(),
            new TupleTypeSpec());

        var generic = new NamedTypeSpec("TestModule.Box");
        generic.GenericParameters.Add(closure);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithDataArg_ReturnsFalse()
    {
        // Foundation.Data implements ISwiftObject at runtime — should NOT be blocked
        var generic = new NamedTypeSpec("TestModule.DataTask");
        generic.GenericParameters.Add(new NamedTypeSpec("Foundation.Data"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithObjCBridgedArg_ReturnsTrue()
    {
        var generic = new NamedTypeSpec("TestModule.Future");
        generic.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithNestedObjCBridgedArgInOptional_ReturnsFalse()
    {
        // Container<Optional<UIView>> — SwiftOptional<UIView> implements ISwiftObject,
        // and UIView inside Optional is fine because SwiftOptional<T> has no constraint on T.
        var inner = new NamedTypeSpec("Swift.Optional");
        inner.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        var outer = new NamedTypeSpec("TestModule.Container");
        outer.GenericParameters.Add(inner);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(outer));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithNestedObjCBridgedArgInArray_ReturnsFalse()
    {
        // Container<Array<UIView>> — SwiftArray<T> has no ISwiftObject constraint on T,
        // and UIView is ObjC-bridged with a projection (UIView → IntPtr). The bypass
        // recognizes Array<UIView> as a container with projectable elements.
        var inner = new NamedTypeSpec("Swift.Array");
        inner.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        var outer = new NamedTypeSpec("TestModule.Container");
        outer.GenericParameters.Add(inner);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(outer));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithNamedVoidInNonOptional_ReturnsTrue()
    {
        // Result<Swift.Void, Error> — SwiftVoid doesn't implement ISwiftObject
        var generic = new NamedTypeSpec("TestModule.Result");
        generic.GenericParameters.Add(new NamedTypeSpec("Swift.Void"));
        generic.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithEmptyTupleInNonOptional_ReturnsTrue()
    {
        // Result<(), Error> — empty tuple maps to SwiftVoid, doesn't implement ISwiftObject
        var generic = new NamedTypeSpec("TestModule.Result");
        generic.GenericParameters.Add(TupleTypeSpec.Empty);
        generic.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithNamedVoidInOptional_ReturnsFalse()
    {
        // Optional<Swift.Void> — SwiftOptional has no ISwiftObject constraint
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Swift.Void"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithEmptyTupleInOptional_ReturnsFalse()
    {
        // Optional<()> — SwiftOptional has no ISwiftObject constraint
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(TupleTypeSpec.Empty);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NestedGenericOwner_QualifiesOwnerArguments()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var outerDecl = CreateStructDecl("Outer", moduleDecl);
        var innerDecl = CreateNestedGenericStructDecl("Inner", moduleDecl, outerDecl, "T", "U");
        _ = CreateNestedGenericStructDecl("Leaf", moduleDecl, innerDecl, "X", "Y");

        var leafTypeSpec = new NamedTypeSpec("TestModule.Outer.Inner.Leaf");
        leafTypeSpec.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        leafTypeSpec.GenericParameters.Add(new NamedTypeSpec("τ_0_1"));

        var propertyDecl = new PropertyDecl
        {
            Name = "leaf",
            SwiftTypeSpec = leafTypeSpec,
            ParentDecl = innerDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var result = _handler.TranslateBoundGenericTypeToCSharp(
            propertyDecl,
            GenericContext.FromType(innerDecl));

        Assert.Equal("TestModule.Outer.Inner<T, U>.Leaf<T, U>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_InnerTypeChainOnGenericOuter_PlacesArgsOnOuterSegment()
    {
        // Regression for the nested-type-on-generic-outer reference shape
        // (StoreKit2.VerificationResult<SignedType>.VerificationError).
        //
        // ABI parser produces: NamedTypeSpec("TestModule.Outer") with
        // GenericParameters=[Swift.String] and InnerType=NamedTypeSpec("Nested"), representing
        // "Outer<String>.Nested" at the reference site. The outer's generic args belong to
        // the OUTER segment of the dotted C# name, not the leaf. Before the fix, the emitter
        // appended <Swift.SwiftString> at the END, producing the invalid "Outer.Nested<Swift.SwiftString>".
        var moduleDecl = CreateModuleDecl("TestModule");
        var outerDecl = CreateGenericStructDecl("Outer", moduleDecl, "T", "Swift.Equatable");
        _ = CreateNestedStructDecl("Nested", moduleDecl, outerDecl);

        var types = new Dictionary<string, TypeRecord>
        {
            ["Swift.String"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Outer"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Outer.Nested"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer.Nested"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Nested"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var typeSpec = new NamedTypeSpec("TestModule.Outer");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        typeSpec.InnerType = new NamedTypeSpec("Nested");

        var result = handler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty, moduleDecl);

        Assert.Equal("TestModule.Outer<Swift.Runtime.SwiftString>.Nested", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_DoublyGenericNested_PlacesArgsOnBothSegments()
    {
        // Regression for audit P2 (BoundGenericsHandler leaf-arg drop → CS0305): a nested type
        // where BOTH the outer AND the inner (leaf) carry their own generic arguments
        // (Swift "Outer<String>.Inner<Tag>"). The parser encodes this as
        // NamedTypeSpec("TestModule.Outer"){ GenericParameters=[Swift.String],
        // InnerType=NamedTypeSpec("Inner"){ GenericParameters=[TestModule.Tag] } }.
        // Before the fix, only the outer args were placed and the early-return dropped the leaf
        // args entirely — emitting "TestModule.Outer<...>.Inner" (bare leaf), which Roslyn rejects
        // with CS0305 ("using the generic type 'Inner<U>' requires 1 type argument"). Distinct
        // outer/inner arg types pin both placement AND ordering (a swap would also fail).
        var moduleDecl = CreateModuleDecl("TestModule");
        var outerDecl = CreateGenericStructDecl("Outer", moduleDecl, "T", "Swift.Equatable");
        _ = CreateNestedGenericStructDecl("Inner", moduleDecl, outerDecl, "U");

        var types = new Dictionary<string, TypeRecord>
        {
            ["Swift.String"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Tag"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Tag"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Tag"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Outer"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Outer.Inner"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer.Inner"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var typeSpec = new NamedTypeSpec("TestModule.Outer");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        typeSpec.InnerType = new NamedTypeSpec("Inner");
        typeSpec.InnerType.GenericParameters.Add(new NamedTypeSpec("TestModule.Tag"));

        var result = handler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty, moduleDecl);

        Assert.Equal("TestModule.Outer<Swift.Runtime.SwiftString>.Inner<TestModule.Tag>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_SugaredGenericParamInContext_ResolvesToCSharpName()
    {
        // Regression for StoreKit2.VerificationResult<SignedType>.VerificationError:
        // Apple framework ABI JSON lacks sugared_genericSig, so the parser stores
        // "SignedType" as the raw TypeName. GenericContext is keyed by TypeName, so the
        // context maps "SignedType" -> "TSignedType". But "SignedType" fails the strict
        // IsGenericTypeParameter shape check (not τ_X_Y, not T\d+), and if we pre-gate
        // TryResolve on that check, the bare NamedTypeSpec("SignedType") falls through
        // to the typedb and is translated as AnyType — causing the enum factory to emit
        // "Outer<Swift.AnyType>.Inner" instead of "Outer<TSignedType>.Inner".
        var moduleDecl = CreateModuleDecl("TestModule");
        // Simulate the Apple framework parser output: the sugared name appears verbatim
        // in GenericArgumentDecl.TypeName (no sugared_genericSig to remap), matching what
        // SwiftABIParser produces when ingesting a StoreKit-style ABI JSON.
        var outerDecl = CreateStructDecl("Outer", moduleDecl);
        outerDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(TypeName: "SignedType",
                SugaredTypeName: "SignedType",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        _ = CreateNestedStructDecl("Nested", moduleDecl, outerDecl);

        var types = new Dictionary<string, TypeRecord>
        {
            ["TestModule.Outer"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["TestModule.Outer.Nested"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer.Nested"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Nested"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var typeSpec = new NamedTypeSpec("TestModule.Outer");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("SignedType"));
        typeSpec.InnerType = new NamedTypeSpec("Nested");

        var context = new GenericContext(new Dictionary<string, GenericParameterCSName>
        {
            ["SignedType"] = new GenericParameterCSName("TSignedType")
        });

        var result = handler.TranslateBoundGenericTypeToCSharp(typeSpec, context, moduleDecl);

        Assert.Equal("TestModule.Outer<TSignedType>.Nested", result);
        Assert.DoesNotContain("AnyType", result);
    }

    #endregion

    #region BG4 — Optional Tuple with Existential Element

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalTupleWithAny_ReturnsTrue()
    {
        // B5 skip gate: Optional<(Int, Any)> — the existential 'Any' inside a tuple
        // inside Optional should be detected as a non-SwiftObject generic arg.
        // Any maps to "object" which can't satisfy ISwiftObject.
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(new ProtocolListTypeSpec()); // Any

        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(tuple);

        Assert.True(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalTupleWithSupportedProtocol_ReturnsTrue()
    {
        // Optional<(Int, any Equatable)> — protocol existentials that map to "object"
        // inside tuples inside Optional should still be flagged.
        var equatable = new ProtocolListTypeSpec();
        equatable.Protocols.Add(new NamedTypeSpec("Swift.Equatable"), true);

        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(equatable);

        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(tuple);

        Assert.True(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalTupleWithoutExistential_ReturnsFalse()
    {
        // Optional<(Int, Bool)> — no existential elements, should be fine.
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(new NamedTypeSpec("Swift.Bool"));

        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(tuple);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalObjCBridged_ReturnsFalse()
    {
        // Optional<UIKit.UIView> — SwiftOptional<T> has no ISwiftObject constraint,
        // so ObjC-bridged types are valid inside Optional even though they'd fail in SwiftArray<T>.
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalNativeRemapped_ReturnsFalse()
    {
        // Optional<Foundation.URL> — SwiftOptional<T> has no ISwiftObject constraint,
        // so native-remapped types (URL → NSUrl) are valid inside Optional.
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Foundation.URL"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ArrayOfObjCBridged_ReturnsFalse()
    {
        // SwiftArray<UIKit.UIView> — SwiftArray<T> has no ISwiftObject constraint on T,
        // and UIView is ObjC-bridged with a projection. Container bypass applies.
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(array));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ArrayOfNativeRemapped_ReturnsTrue()
    {
        // SwiftArray<Foundation.URL> — native-remapped types in containers are still blocked.
        // The bypass only handles ObjC-bridged class types, not native-remapped types
        // which need different container marshalling not yet supported.
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new NamedTypeSpec("Foundation.URL"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(array));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalUIImage_ReturnsFalse()
    {
        // Optional<UIKit.UIImage> — common pattern for optional image parameters.
        // UIImage is ObjC-bridged but valid inside Optional (no ISwiftObject constraint).
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("UIKit.UIImage"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    #endregion

    #region Container with Projectable Elements Bypass

    [Fact]
    public void HasNonSwiftObjectGenericArg_DictionaryWithObjCBridgedAndNormal_ReturnsFalse()
    {
        // Swift.Dictionary<Swift.String, UIKit.UIView> — String is a normal ISwiftObject type,
        // UIView is ObjC-bridged (→ IntPtr). The bypass applies.
        var dict = new NamedTypeSpec("Swift.Dictionary");
        dict.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dict.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(dict));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalArrayOfObjCBridged_ReturnsFalse()
    {
        // Optional<Array<UIView>> — unwrap Optional, then Array with projectable element.
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(array);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optional));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_NestedArrayOfObjCBridged_ReturnsFalse()
    {
        // Array<Array<UIView>> — nested container, recursive bypass.
        var inner = new NamedTypeSpec("Swift.Array");
        inner.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));
        var outer = new NamedTypeSpec("Swift.Array");
        outer.GenericParameters.Add(inner);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(outer));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_SetOfNativeRemapped_ReturnsTrue()
    {
        // Swift.Set<Foundation.URL> — native-remapped types in containers are still blocked.
        // The container bypass only handles ObjC-bridged class types (→ IntPtr), not
        // native-remapped types which need different marshalling.
        var set = new NamedTypeSpec("Swift.Set");
        set.GenericParameters.Add(new NamedTypeSpec("Foundation.URL"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(set));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ArrayOfNonProjectable_ReturnsTrue()
    {
        // Swift.Array<(Int, String)> — tuple element has no projection, bypass doesn't apply.
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new TupleTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        }));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(array));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_NonContainerWithObjCBridged_StillBlocked()
    {
        // TestModule.Future<UIView> — not a container, ObjC-bridged still blocked.
        var generic = new NamedTypeSpec("TestModule.Future");
        generic.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_MeasurementWithUnitTemperature_ReturnsFalse()
    {
        // Measurement<UnitTemperature> — dedicated bypass. The C# Measurement<T> type
        // uses VWT-backed storage and has no ISwiftObject constraint on T.
        var measurement = new NamedTypeSpec("Foundation.Measurement");
        measurement.GenericParameters.Add(new NamedTypeSpec("Foundation.UnitTemperature"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(measurement));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_MeasurementWithUnitLength_ReturnsFalse()
    {
        // Measurement<UnitLength> — same bypass as UnitTemperature.
        var measurement = new NamedTypeSpec("Foundation.Measurement");
        measurement.GenericParameters.Add(new NamedTypeSpec("Foundation.UnitLength"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(measurement));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_NonMeasurementGenericWithObjCArg_StillBlocked()
    {
        // TestModule.Wrapper<UnitTemperature> — NOT Measurement, ObjC-bridged arg still blocked.
        // Verifies the Measurement bypass is specific and doesn't relax constraints broadly.
        var generic = new NamedTypeSpec("TestModule.Wrapper");
        generic.GenericParameters.Add(new NamedTypeSpec("Foundation.UnitTemperature"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    #endregion

    #region Well-Known Stdlib Conformances

    [Theory]
    [InlineData("Swift.String", "Swift.Comparable")]
    [InlineData("Swift.String", "Swift.Equatable")]
    [InlineData("Swift.String", "Swift.Hashable")]
    [InlineData("Swift.Int", "Swift.Comparable")]
    [InlineData("Swift.Int", "Swift.Equatable")]
    [InlineData("Swift.Double", "Swift.Comparable")]
    [InlineData("Swift.Bool", "Swift.Equatable")]
    [InlineData("Swift.Never", "Swift.Error")]
    public void TryGetFirstUnsatisfiedConstraint_WellKnownStdlibConformance_ReturnsFalse(
        string typeArg, string protocolConstraint)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Wrapper", moduleDecl, "T", protocolConstraint);

        var boundGeneric = new NamedTypeSpec(
            "TestModule.Wrapper",
            new NamedTypeSpec(typeArg));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_StdlibTypeWithoutKnownConformance_ReturnsTrue()
    {
        // Swift.String does NOT conform to Swift.Error — should still be blocked.
        var moduleDecl = CreateModuleDecl("TestModule");
        CreateGenericStructDecl("Wrapper", moduleDecl, "T", "Swift.Error");

        var boundGeneric = new NamedTypeSpec(
            "TestModule.Wrapper",
            new NamedTypeSpec("Swift.String"));
        var contextDecl = CreatePropertyContext(boundGeneric, moduleDecl);

        var found = _handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, contextDecl, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    #endregion

    #region ObjC-Bridged IntPtr Projection in Containers

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayOfObjCBridged_UsesIntPtr()
    {
        // Swift.Array<UIKit.UIView> — ObjC-bridged class elements in stdlib containers
        // should project to IntPtr (the raw pointer representation).
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Equal("Swift.Runtime.SwiftArray<IntPtr>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithObjCBridgedValue_UsesIntPtr()
    {
        // Swift.Dictionary<Swift.String, UIKit.UIView> — ObjC-bridged value element uses IntPtr.
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        var argDecl = CreateArgumentDecl(dictTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("IntPtr", result);
        Assert.Contains("SwiftDictionary", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayOfNativeRemapped_DoesNotUseIntPtr()
    {
        // Swift.Array<Foundation.URL> — native-remapped types should NOT be replaced with IntPtr.
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Foundation.URL"));

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.DoesNotContain("IntPtr", result);
    }

    #endregion

    #region BG6 — Nested Generic Owner Qualification

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_SingleLevelNesting_NoOwnerQualification()
    {
        // Non-nested generic type — no owner qualification needed
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateGenericStructDecl("Box", moduleDecl, "T", "Swift.Equatable");

        var typeSpec = new NamedTypeSpec("TestModule.Box");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var argDecl = CreateArgumentDecl(typeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Should be the translated type without owner qualification
        Assert.Contains("Box", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NestedInNonGenericOwner_NoQualification()
    {
        // Nested generic inside a non-generic owner — owner doesn't need qualification
        var moduleDecl = CreateModuleDecl("TestModule");
        var outerDecl = CreateStructDecl("Container", moduleDecl);
        var innerDecl = CreateNestedGenericStructDecl("Item", moduleDecl, outerDecl, "T");

        var typeSpec = new NamedTypeSpec("TestModule.Container.Item");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var propertyDecl = new PropertyDecl
        {
            Name = "item",
            SwiftTypeSpec = typeSpec,
            ParentDecl = outerDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var result = _handler.TranslateBoundGenericTypeToCSharp(
            propertyDecl,
            GenericContext.Empty);

        // Non-generic owner shouldn't get qualified
        Assert.Contains("Container.Item", result);
        Assert.DoesNotContain("Container<", result);
    }

    #endregion

    #region Multi-Type-Argument Bound Generic Tests

    [Fact]
    public void IsBoundGeneric_MultiTypeArgGeneric_ReturnsTrue()
    {
        // Pair<Int32, String> — two type arguments
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var argDecl = CreateArgumentDecl(pairTypeSpec);
        var result = _handler.IsBoundGeneric(argDecl);

        Assert.True(result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_MultiTypeArgs_FallsBackForUnknownContainer()
    {
        // Pair<Int32, String> — TestModule.Pair is not in TypeDatabase,
        // so the handler falls back to AnyType (same as any unknown bound generic).
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var argDecl = CreateArgumentDecl(pairTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Unknown container types fall back to AnyType
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void IsBoundGeneric_MultiTypeArgProperty_ReturnsTrue()
    {
        // Property of type Pair<Int, String>
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var propertyDecl = CreatePropertyDecl(pairTypeSpec);
        var result = _handler.IsBoundGeneric(propertyDecl);

        Assert.True(result);
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_MultiTypeWithPrimitive_ReturnsFalse()
    {
        // Pair<Int32, String> — Int32 and String are Swift types that implement ISwiftObject.
        // HasNonSwiftObjectGenericArg checks for types like tuples, void, ObjC-bridged that
        // can't satisfy ISwiftObject constraint. Primitives and strings are fine.
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.False(_handler.HasNonSwiftObjectGenericArg(pairTypeSpec));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_MultiTypeOptional_ReturnsFalse()
    {
        // Optional<Pair<Int32, String>> — Optional has no ISwiftObject constraint
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(pairTypeSpec);

        Assert.False(_handler.HasNonSwiftObjectGenericArg(optionalSpec));
    }

    [Fact]
    public void TryGetFirstExistentialTypeArgument_MultiTypeNoExistential_ReturnsFalse()
    {
        // Pair<Int32, String> — no existential type arguments
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var found = _handler.TryGetFirstExistentialTypeArgument(pairTypeSpec, out var existentialType);

        Assert.False(found);
        Assert.Equal(string.Empty, existentialType);
    }

    [Fact]
    public void TryGetFirstExistentialTypeArgument_MultiTypeWithExistential_ReturnsTrue()
    {
        // Pair<Int, any Equatable> — second type arg is an existential
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        pairTypeSpec.GenericParameters.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") }));

        var found = _handler.TryGetFirstExistentialTypeArgument(pairTypeSpec, out var existentialType);

        Assert.True(found);
        Assert.Equal("Swift.Equatable", existentialType);
    }

    [Fact]
    public void GetBufferType_MultiTypeArgGeneric_ReturnsIntPtr()
    {
        // Pair<Int32, String> is not in s_bufferTypeMap → fallback to IntPtr
        var pairTypeSpec = new NamedTypeSpec("TestModule.Pair");
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        pairTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var argDecl = CreateArgumentDecl(pairTypeSpec);
        var result = _handler.GetBufferType(argDecl);

        Assert.Equal("IntPtr", result);
    }

    #endregion

    #region Helper Methods

    private static ArgumentDecl CreateArgumentDecl(TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = "testArg",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(TypeSpec typeSpec)
    {
        return new PropertyDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = "testProperty",
            ParentDecl = null,
            ModuleDecl = null,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };
    }

    private static PropertyDecl CreatePropertyContext(TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = "context",
            SwiftTypeSpec = typeSpec,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };
    }

    private static ModuleDecl CreateModuleDecl(string moduleName)
    {
        return new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string structName, ModuleDecl moduleDecl, IEnumerable<string> protocolConformances = null)
    {
        var conformances = protocolConformances?.Select(protocol => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{structName}"),
            SwiftTypeName.FromModuleQualifiedName(protocol),
            ProtocolConformanceDescriptor: string.Empty)).ToList()
            ?? new List<TypeConformance>();

        var structDecl = new StructDecl
        {
            Name = structName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{structName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateGenericStructDecl(string structName, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
    {
        var structDecl = CreateStructDecl(structName, moduleDecl);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: typeParameterName,
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(
                        Path: new[] { "τ_0_0" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(constraintProtocolName),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        return structDecl;
    }

    private static ClassDecl CreateClassDecl(string className, ModuleDecl moduleDecl, IEnumerable<string> protocolConformances = null)
    {
        var conformances = protocolConformances?.Select(protocol => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{className}"),
            SwiftTypeName.FromModuleQualifiedName(protocol),
            ProtocolConformanceDescriptor: string.Empty)).ToList()
            ?? new List<TypeConformance>();

        var classDecl = new ClassDecl
        {
            Name = className,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{className}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{className.Length}{className}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static EnumDecl CreateEnumDecl(string enumName, ModuleDecl moduleDecl, IEnumerable<string> protocolConformances = null)
    {
        var conformances = protocolConformances?.Select(protocol => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{enumName}"),
            SwiftTypeName.FromModuleQualifiedName(protocol),
            ProtocolConformanceDescriptor: string.Empty)).ToList()
            ?? new List<TypeConformance>();

        var enumDecl = new EnumDecl
        {
            Name = enumName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{enumName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{enumName.Length}{enumName}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = conformances,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{enumName.Length}{enumName}OMa"
        };
        moduleDecl.Types.Add(enumDecl);
        return enumDecl;
    }

    private static StructDecl CreateNestedStructDecl(
        string structName,
        ModuleDecl moduleDecl,
        TypeDecl parentDecl)
    {
        var structDecl = new StructDecl
        {
            Name = structName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{parentDecl.SwiftTypeName.ModuleQualifiedName}.{structName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };
        parentDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateNestedGenericStructDecl(
        string structName,
        ModuleDecl moduleDecl,
        TypeDecl parentDecl,
        params string[] typeParameterNames)
    {
        var structDecl = new StructDecl
        {
            Name = structName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{parentDecl.SwiftTypeName.ModuleQualifiedName}.{structName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = typeParameterNames
                .Select((name, i) => new GenericArgumentDecl(
                    TypeName: $"τ_0_{i}",
                    SugaredTypeName: name,
                    GenericConformances: new List<GenericParameterConformance>(),
                    AssosiatedTypeConformances: new List<GenericParameterConformance>()))
                .ToList(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };

        parentDecl.Types.Add(structDecl);
        return structDecl;
    }

    #endregion

    #region Conformance Oracle (fail-closed trichotomy)

    // These pin the {Yes, No, Unknown} distinction the public TryGetFirstUnsatisfiedConstraint
    // path collapses to satisfied/not. The key fail-closed contract: a fully resolved local type
    // lacking the conformance is a DEFINITIVE No (Swift never promised it), while a foreign type
    // with no fact source is Unknown (genuinely unprovable) — both drop, but they are different
    // facts. Yes for a stdlib pair exercises the committed stdlib-conformances.json fact table.

    [Fact]
    public void ConformanceOracle_StdlibFact_ReturnsYes()
    {
        // Swift.String : Swift.Comparable is recorded in the committed stdlib fact table.
        // String has no local TypeDecl, so the oracle resolves it from the embedded facts.
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        var module = CreateModuleDecl("TestModule");

        var result = oracle.ConcreteConforms(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Comparable"),
            constraintRecord: null,
            typeArgumentDecl: null,
            moduleDecl: module);

        Assert.Equal(ConformanceResult.Yes, result);
    }

    [Fact]
    public void ConformanceOracle_FullyResolvedLocalTypeWithoutConformance_ReturnsNo()
    {
        // A local TypeDecl that declares no matching conformance is a DEFINITIVE negative,
        // not merely unprovable.
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        var module = CreateModuleDecl("TestModule");
        var rawData = CreateStructDecl("RawData", module); // no conformances

        var result = oracle.ConcreteConforms(
            SwiftTypeName.FromModuleQualifiedName("TestModule.RawData"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Serializable"),
            constraintRecord: null,
            typeArgumentDecl: rawData,
            moduleDecl: module);

        Assert.Equal(ConformanceResult.No, result);
    }

    [Fact]
    public void ConformanceOracle_ForeignTypeNoFact_ReturnsUnknown()
    {
        // A foreign type with no local decl and no fact source (stdlib table or stripped
        // conformance) is genuinely unprovable — the oracle fails closed with Unknown,
        // distinct from a definitive No.
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        var module = CreateModuleDecl("TestModule");

        var result = oracle.ConcreteConforms(
            SwiftTypeName.FromModuleQualifiedName("ThirdParty.Mystery"),
            SwiftTypeName.FromModuleQualifiedName("ThirdParty.SomeProtocol"),
            constraintRecord: null,
            typeArgumentDecl: null,
            moduleDecl: module);

        Assert.Equal(ConformanceResult.Unknown, result);
    }

    [Fact]
    public void ConformanceOracle_LocalDirectConformance_ReturnsYes()
    {
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        var module = CreateModuleDecl("TestModule");
        var dataModel = CreateStructDecl("DataModel", module, new[] { "TestModule.Serializable" });

        var result = oracle.ConcreteConforms(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataModel"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Serializable"),
            constraintRecord: null,
            typeArgumentDecl: dataModel,
            moduleDecl: module);

        Assert.Equal(ConformanceResult.Yes, result);
    }

    [Fact]
    public void ConformanceOracle_SelfConformanceForeignProtocol_ReturnsYes()
    {
        // A protocol type used as its own constraint (typeArgument == constraint) resolves Yes
        // even with no local decl.
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        var module = CreateModuleDecl("TestModule");
        var p = SwiftTypeName.FromModuleQualifiedName("ThirdParty.SomeProtocol");

        var result = oracle.ConcreteConforms(p, p, constraintRecord: null, typeArgumentDecl: null, moduleDecl: module);

        Assert.Equal(ConformanceResult.Yes, result);
    }

    [Theory]
    [InlineData("Swift.String", "Swift.Hashable")]
    [InlineData("Swift.Int", "Swift.FixedWidthInteger")]
    [InlineData("Swift.Double", "Swift.BinaryFloatingPoint")]
    [InlineData("Swift.Bool", "Swift.ExpressibleByBooleanLiteral")]
    [InlineData("Swift.Never", "Swift.Error")]
    public void ConformanceOracle_HasStdlibConformance_SeededFacts_ReturnTrue(string type, string protocol)
    {
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        Assert.True(oracle.HasStdlibConformance(
            SwiftTypeName.FromModuleQualifiedName(type),
            SwiftTypeName.FromModuleQualifiedName(protocol)));
    }

    [Fact]
    public void ConformanceOracle_HasStdlibConformance_UnknownPair_ReturnsFalse()
    {
        var oracle = new ConformanceOracle(new MockTypeDatabase());
        // Swift.Int does not conform to Swift.FloatingPoint.
        Assert.False(oracle.HasStdlibConformance(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            SwiftTypeName.FromModuleQualifiedName("Swift.FloatingPoint")));
        // A stdlib type entirely absent from the table.
        Assert.False(oracle.HasStdlibConformance(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt8"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Comparable")));
    }

    [Fact]
    public void ConformanceOracle_Construction_LoadsStdlibFactTable_SchemaHandshakePasses()
    {
        // The embedded stdlib-conformances.json must load and its schemaVersion must match
        // ExpectedStdlibSchemaVersion — a mismatch throws at construction.
        var ex = Record.Exception(() => new ConformanceOracle(new MockTypeDatabase()));
        Assert.Null(ex);
        Assert.Equal(1, ConformanceOracle.ExpectedStdlibSchemaVersion);
    }

    #endregion

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Dictionary"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftDictionary"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Result"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftResult"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Error"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "ISwiftError"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                },
                ["TestModule.Outer.Inner.Leaf"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer.Inner.Leaf"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner.Leaf"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.Container.Item"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container.Item"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container.Item"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.Box"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Foundation.URL"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                    NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // SIMD alias target — Swift.SIMD3<Swift.Float> collapses to this non-generic record
                // via the shared BoundGenericTranslation.TryResolveSimdAliasCSharp short-circuit.
                ["simd.simd_float3"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region HasMethodSelfTypeParams — Constraint Skipping Tests

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ProtocolWithMethodSelfTypeParams_SkipsConstraint()
    {
        // Protocol with HasMethodSelfTypeParams flag (methods use τ_0_0 in signatures,
        // e.g., VectorAnimation.AnyInterpolatable._interpolate). The constraint on the bound
        // generic should be skipped because ShouldSkipConstraint returns true for
        // HasMethodSelfTypeParams.
        var types = new Dictionary<string, TypeRecord>
        {
            ["VectorAnimation.AnyInterpolatable"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("VectorAnimation", "IAnyInterpolatable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.AnyInterpolatable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasMethodSelfTypeParams,
                Kind = TypeRecordKind.Protocol
            },
            ["VectorAnimation.ValueProviderStorage"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("VectorAnimation", "ValueProviderStorage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.ValueProviderStorage"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        // Build the generic struct declaration: ValueProviderStorage<T> where T: AnyInterpolatable
        var storageTypeDecl = new StructDecl
        {
            Name = "ValueProviderStorage",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.ValueProviderStorage"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        storageTypeDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("VectorAnimation.AnyInterpolatable"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var moduleDecl = new ModuleDecl
        {
            Name = "VectorAnimation",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { storageTypeDecl },
            Dependencies = new(),
            Protocols = new()
        };
        storageTypeDecl.ModuleDecl = moduleDecl;

        // Parent type with generic param T (no constraints — doesn't matter for skip)
        var parentType = new StructDecl
        {
            Name = "SomeContainer",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.SomeContainer"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };

        var boundGeneric = new NamedTypeSpec("VectorAnimation.ValueProviderStorage", new NamedTypeSpec("τ_0_0"));

        var method = new MethodDecl
        {
            Name = "testMethod",
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out _);

        // HasMethodSelfTypeParams causes the constraint to be SKIPPED (not checked),
        // so no unsatisfied constraint is found.
        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ProtocolWithoutMethodSelfTypeParams_ChecksConstraint()
    {
        // Regular protocol without HasMethodSelfTypeParams — constraint IS checked.
        // If the parent type doesn't declare the conformance, it's unsatisfied.
        var types = new Dictionary<string, TypeRecord>
        {
            ["TestModule.Sortable"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ISortable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Sortable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            },
            ["TestModule.SortedList"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SortedList"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SortedList"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        // Build the generic struct declaration: SortedList<T> where T: Sortable
        var sortedListDecl = new StructDecl
        {
            Name = "SortedList",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SortedList"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        sortedListDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("TestModule.Sortable"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { sortedListDecl },
            Dependencies = new(),
            Protocols = new()
        };
        sortedListDecl.ModuleDecl = moduleDecl;

        // Parent type with generic param T but NO Sortable conformance
        var parentType = new StructDecl
        {
            Name = "Container",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        parentType.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>(), // No constraints
            new List<GenericParameterConformance>()));

        var boundGeneric = new NamedTypeSpec("TestModule.SortedList", new NamedTypeSpec("τ_0_0"));

        var method = new MethodDecl
        {
            Name = "sort",
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        // Regular protocol constraint IS checked and found unsatisfied.
        Assert.True(found);
        Assert.Contains("Sortable", details);
    }

    private class MockTypeDatabaseWithCustomTypes : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabaseWithCustomTypes(Dictionary<string, TypeRecord> types)
        {
            _types = types;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region ConformanceUnreachableInCSharp — Swift Extension on Foreign Type

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ExtensionOnForeignType_RejectsUnreachableConformance()
    {
        // Regression: `extension Foundation.Data: DataTransformable` adds Swift-level
        // conformance via a local TypeDecl, but C# cannot retrofit an interface onto a type
        // from another assembly. Binding Backend<Foundation.Data> with
        // `where T: IDataTransformable` produces CS0315 at consumer build time. Detect and
        // skip the member instead.
        var types = new Dictionary<string, TypeRecord>
        {
            ["ImageLoader.DataTransformable"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "IDataTransformable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            },
            ["ImageLoader.Backend"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "Backend"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            },
            ["Foundation.Data"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var backendDecl = new StructDecl
        {
            Name = "Backend",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        backendDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        // The Swift extension shows up as a local StructDecl in the ImageLoader module
        // whose SwiftTypeName points at the foreign type Foundation.Data and carries
        // the DataTransformable conformance — exactly what the parser produces for
        // `extension Foundation.Data: DataTransformable`.
        var foreignExtensionDecl = new StructDecl
        {
            Name = "Data",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = true,
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"),
                    ProtocolConformanceDescriptor: "")
            },
            MetadataAccessor = ""
        };

        var moduleDecl = new ModuleDecl
        {
            Name = "ImageLoader",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { backendDecl, foreignExtensionDecl },
            Dependencies = new(),
            Protocols = new()
        };
        backendDecl.ModuleDecl = moduleDecl;
        foreignExtensionDecl.ModuleDecl = moduleDecl;

        // DiskStorage holds a Backend<Foundation.Data> field — the binding site
        // where the constraint must hold.
        var diskStorage = new StructDecl
        {
            Name = "DiskStorage",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.DiskStorage"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };

        var boundGeneric = new NamedTypeSpec("ImageLoader.Backend", new NamedTypeSpec("Foundation.Data"));

        var method = new MethodDecl
        {
            Name = "getBackend",
            ParentDecl = diskStorage,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        // The local extension makes SatisfiesConstraint return true, but the conformance
        // is unreachable in C# — must be rejected, leaving the member dropped.
        Assert.True(found);
        Assert.Contains("DataTransformable", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_NativeTypeArgumentWithLocalConformance_NotRejected()
    {
        // Counter-test: the unreachable-conformance filter MUST NOT fire when the
        // type argument lives in the same module as the constraint's evidence.
        // `extension ImageLoader.PNGImage: DataTransformable` is reachable in C# —
        // the C# `ImageLoader.PNGImage` class actually implements `IDataTransformable`.
        var types = new Dictionary<string, TypeRecord>
        {
            ["ImageLoader.DataTransformable"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "IDataTransformable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            },
            ["ImageLoader.Backend"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "Backend"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            },
            ["ImageLoader.PNGImage"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "PNGImage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.PNGImage"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var backendDecl = new StructDecl
        {
            Name = "Backend",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        backendDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var pngImageDecl = new StructDecl
        {
            Name = "PNGImage",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.PNGImage"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("ImageLoader.PNGImage"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("ImageLoader.DataTransformable"),
                    ProtocolConformanceDescriptor: "")
            },
            MetadataAccessor = ""
        };

        var moduleDecl = new ModuleDecl
        {
            Name = "ImageLoader",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { backendDecl, pngImageDecl },
            Dependencies = new(),
            Protocols = new()
        };
        backendDecl.ModuleDecl = moduleDecl;
        pngImageDecl.ModuleDecl = moduleDecl;

        var diskStorage = new StructDecl
        {
            Name = "DiskStorage",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.DiskStorage"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };

        var boundGeneric = new NamedTypeSpec("ImageLoader.Backend", new NamedTypeSpec("ImageLoader.PNGImage"));

        var method = new MethodDecl
        {
            Name = "getBackend",
            ParentDecl = diskStorage,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out _);

        // Local module + local conformance → reachable in C#, member must NOT be skipped.
        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ClassBoundConstraintAcrossModules_NotRejected()
    {
        // Class-bound constraints (`<T : SomeClass>`) flow through C# inheritance,
        // not interface implementation — the "extension on foreign type" filter is
        // interface-shaped and must not apply. Without the protocol-record gate,
        // `Box<PDFKit.PDFView>` for `Box<T: UIKit.UIView>` emitted from a third
        // module would be rejected because PDFKit ≠ emittingModule and UIKit ≠ PDFKit
        // triggers the module-difference heuristic.
        var types = new Dictionary<string, TypeRecord>
        {
            ["UIKit.UIView"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIView"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            },
            ["PDFKit.PDFView"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("PDFKit", "PDFView"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("PDFKit.PDFView"),
                MetadataAccessor = "",
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"),
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            },
            ["ThirdParty.Box"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ThirdParty", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ThirdParty.Box"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var boxDecl = new StructDecl
        {
            Name = "Box",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ThirdParty.Box"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        boxDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                // Parser tags `T : UIView` as ConformanceKind.Protocol — the gate
                // distinguishes class vs protocol by the TypeRecord.Kind, not by ConformanceKind.
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var moduleDecl = new ModuleDecl
        {
            Name = "ThirdParty",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { boxDecl },
            Dependencies = new(),
            Protocols = new()
        };
        boxDecl.ModuleDecl = moduleDecl;

        var container = new StructDecl
        {
            Name = "Container",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ThirdParty.Container"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };

        var boundGeneric = new NamedTypeSpec("ThirdParty.Box", new NamedTypeSpec("PDFKit.PDFView"));

        var method = new MethodDecl
        {
            Name = "wrap",
            ParentDecl = container,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out _);

        // Class subtyping IS reachable in C# — the gate must NOT fire on class-bound constraints.
        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ForeignTypeArgumentForeignProtocol_NotRejected()
    {
        // Second counter-test: when the protocol constraint also belongs to the
        // foreign module (e.g. Foundation.Data : Foundation.ContiguousBytes), the
        // conformance is owned by that module's projection, not the emitting one.
        // The filter must not fire on this shape — only on the
        // local-extension-on-foreign-type case.
        var types = new Dictionary<string, TypeRecord>
        {
            ["Foundation.ContiguousBytes"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "IContiguousBytes"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.ContiguousBytes"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            },
            ["ImageLoader.Backend"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImageLoader", "Backend"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            },
            ["Foundation.Data"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            }
        };
        var db = new MockTypeDatabaseWithCustomTypes(types);
        var handler = new BoundGenericsHandler(db);

        var backendDecl = new StructDecl
        {
            Name = "Backend",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.Backend"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };
        backendDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>
            {
                new(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Foundation.ContiguousBytes"), ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        // Foundation.Data carries the Foundation.ContiguousBytes conformance — represents
        // a stdlib pairing that is reachable through the Apple supplement's projection.
        var foundationDataDecl = new StructDecl
        {
            Name = "Data",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = true,
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Foundation.ContiguousBytes"),
                    ProtocolConformanceDescriptor: "")
            },
            MetadataAccessor = ""
        };

        var moduleDecl = new ModuleDecl
        {
            Name = "ImageLoader",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new(),
            Methods = new(),
            Types = new List<TypeDecl> { backendDecl, foundationDataDecl },
            Dependencies = new(),
            Protocols = new()
        };
        backendDecl.ModuleDecl = moduleDecl;
        foundationDataDecl.ModuleDecl = moduleDecl;

        var diskStorage = new StructDecl
        {
            Name = "DiskStorage",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImageLoader.DiskStorage"),
            MangledName = "",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            IsFrozen = false,
            Conformances = new(),
            MetadataAccessor = ""
        };

        var boundGeneric = new NamedTypeSpec("ImageLoader.Backend", new NamedTypeSpec("Foundation.Data"));

        var method = new MethodDecl
        {
            Name = "getBackend",
            ParentDecl = diskStorage,
            ModuleDecl = moduleDecl,
            MangledName = "",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };

        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out _);

        // Foreign-type + foreign-protocol → constraint protocol is owned by the
        // foreign module's projection. Filter MUST NOT fire here.
        Assert.False(found);
    }

    #endregion

    #region IsContainerWithSupportedDirectExistential — KeyPath family

    // Build a TypeDatabase populated for KeyPath<any P, V> admission tests.
    // Includes Swift.String / Swift.Int / Swift.Bool / Swift.Double for V projection,
    // every KeyPath family class as a Class kind (so projection's KeyPath branch
    // hits), and a registered protocol P with a TypeRecord so IsExistential gates pass.
    private static TypeDatabase BuildKeyPathAdmissionTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.P"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IP"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.P"),
                MetadataAccessor = "$s10TestModule1PMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Unprojectable"),
            new TypeRecord
            {
                // Intentionally projection-hostile: no Apple-supplement, generic-args present,
                // no module path. Project() returns null because the TypeRecord exists but
                // the factory's bound-generic fallback path produces no projection for an
                // arbitrary user struct used as a KeyPath Value slot.
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Unprojectable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Unprojectable"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_KeyPath_AnyP_String_Admitted()
    {
        // Swift: KeyPath<any P, String> — Root existential, Value projectable.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(root);
        kp.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.True(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_PartialKeyPath_AnyP_Admitted()
    {
        // Swift: PartialKeyPath<any P> — arity 1, Root existential.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.PartialKeyPath");
        kp.GenericParameters.Add(root);

        Assert.True(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_WritableKeyPath_AnyP_Int_Admitted()
    {
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.WritableKeyPath");
        kp.GenericParameters.Add(root);
        kp.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.True(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ReferenceWritableKeyPath_AnyP_Bool_Admitted()
    {
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.ReferenceWritableKeyPath");
        kp.GenericParameters.Add(root);
        kp.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));

        Assert.True(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_AnyKeyPath_Rejected()
    {
        // Swift: AnyKeyPath — arity 0, no Root slot. Cannot be admitted via this gate
        // (it isn't a container-with-existential — it's a class with no generic params).
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var kp = new NamedTypeSpec("Swift.AnyKeyPath");

        Assert.False(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_KeyPath_AnyP_AnyQ_Rejected()
    {
        // Swift: KeyPath<any P, any Q> — Value-existential rejected. A KeyPath whose
        // Value slot is itself existential cannot project to a public C# KeyPath<Root, V>
        // without dragging in another existential bridge (out of scope for this gate).
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var valueExistential = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(root);
        kp.GenericParameters.Add(valueExistential);

        Assert.False(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_KeyPath_ConcreteRoot_Rejected()
    {
        // Swift: KeyPath<ConcreteRoot, String> — Root is NOT existential. This admission
        // gate is for the *existential-rooted* shape; concrete-rooted KeyPaths route
        // through the normal bound-generic path.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(new NamedTypeSpec("TestModule.SomeConcreteRoot"));
        kp.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.False(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_KeyPath_MalformedArity_Rejected()
    {
        // Swift.KeyPath declares arity 2; a NamedTypeSpec with only 1 generic param is
        // malformed input. The arity gate rejects rather than silently admitting a
        // partial shape.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(root);
        // Intentionally missing Value slot.

        Assert.False(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_KeyPath_AnyP_UnprojectableValue_Rejected()
    {
        // Swift: KeyPath<any P, TestModule.Unprojectable>. Value passes the
        // !IsExistential check but Project() returns null because the factory's
        // bound-generic fallback can't resolve a public C# spelling for an arbitrary
        // user struct sitting in the KeyPath Value slot. The reviewer-required
        // projectability gate catches this and rejects so the
        // emitted KeyPath<Root, TValue> public signature can't reference an unspellable
        // TValue.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(root);
        var unprojectable = new NamedTypeSpec("TestModule.Unprojectable");
        // Add a generic parameter to force the factory's user-defined-generic null-fallback.
        unprojectable.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        kp.GenericParameters.Add(unprojectable);

        Assert.False(handler.IsContainerWithSupportedDirectExistential(kp));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_OptionalKeyPath_AnyP_String_Admitted()
    {
        // Swift: Optional<KeyPath<any P, String>> — Optional wraps the existing
        // recursion at line 252-262; once the inner KeyPath shape is admitted, the
        // Optional layer composes for free.
        var db = BuildKeyPathAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var root = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P") });
        var kp = new NamedTypeSpec("Swift.KeyPath");
        kp.GenericParameters.Add(root);
        kp.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(kp);

        Assert.True(handler.IsContainerWithSupportedDirectExistential(optional));
    }

    #endregion

    #region IsContainerWithSupportedDirectExistential — Result family

    // Pin the Result<Success, Failure> admission branch. The canonical shape is Lottie's
    // `Result<DotLottieFile, any Error>`: a bound-class success arm and the well-known
    // `any Error` failure existential. The gate admits a Result iff every existential arm is
    // valid-for-container AND every non-existential arm projects to a real C# type.

    // TypeDatabase populated for Result<...> admission: Swift.String / Swift.Int as projectable
    // value arms, a bound-class LoadedAsset (faithful to Lottie's class success payload), and an
    // Unprojectable user struct that returns a null projection when given generic args.
    private static TypeDatabase BuildResultAdmissionTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.LoadedAsset"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LoadedAsset"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LoadedAsset"),
                MetadataAccessor = "$s10TestModule11LoadedAssetCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Unprojectable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Unprojectable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Unprojectable"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static NamedTypeSpec BuildResultOf(TypeSpec success, TypeSpec failure)
    {
        var result = new NamedTypeSpec("Swift.Result");
        result.GenericParameters.Add(success);
        result.GenericParameters.Add(failure);
        return result;
    }

    private static ProtocolListTypeSpec AnyError() =>
        new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") });

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultStringSuccess_AnyErrorFailure_Admitted()
    {
        // Result<String, any Error> — projectable value success, well-known existential failure.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError());
        Assert.True(handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultClassSuccess_AnyErrorFailure_Admitted()
    {
        // Result<LoadedAsset, any Error> — faithful Lottie shape: bound-class success arm
        // (projects to ClassProjection) + well-known `any Error` failure existential.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("TestModule.LoadedAsset"), AnyError());
        Assert.True(handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultAnySendableFailure_Admitted()
    {
        // Result<String, any Sendable> — marker-only failure arm is zero-witness, admitted via
        // the same ExistentialContainer0 accept as bare Any.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var anySendable = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), anySendable);
        Assert.True(handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultUnprojectableSuccess_Rejected()
    {
        // Result<Unprojectable<Int>, any Error> — the success arm passes !IsExistential but
        // Project() returns null (user struct with generic args hits the factory's null fallback),
        // so the emitted Result<TSuccess, TFailure> signature would not compile → rejected.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var unprojectable = new NamedTypeSpec("TestModule.Unprojectable");
        unprojectable.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var spec = BuildResultOf(unprojectable, AnyError());
        Assert.False(handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultUnknownProtocolFailure_Rejected()
    {
        // Result<String, any UnknownProtocol> — the failure existential has no TypeRecord and is
        // not a well-known/zero-witness existential, so it is invalid-for-container → rejected.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var unknownExistential = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.UnknownProtocol") });
        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), unknownExistential);
        Assert.False(handler.IsContainerWithSupportedDirectExistential(spec));
    }

    [Fact]
    public void IsContainerWithSupportedDirectExistential_ResultWrongArity_Rejected()
    {
        // A Swift.Result with a single generic argument is malformed; the branch requires
        // exactly 2 arms, so it falls through to rejection rather than admitting a partial shape.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var result = new NamedTypeSpec("Swift.Result");
        result.GenericParameters.Add(AnyError());
        Assert.False(handler.IsContainerWithSupportedDirectExistential(result));
    }

    // Result<Success, Failure> is RETURN/property-only: ResultProjection.GetParameterPlan throws
    // and the bound-generic fast path would otherwise pin a meaningless value.Payload, emitting a
    // wrapper that compiles but mis-marshals at runtime. HasNonSwiftObjectGenericArg is the
    // authoritative direction-aware guard: it must DROP a member carrying a Result argument in
    // parameter position (returns true) while LEAVING the return-position Result admitted (false).

    [Fact]
    public void HasNonSwiftObjectGenericArg_ResultAnyErrorParameter_Dropped()
    {
        // Result<String, any Error> in PARAMETER position — the well-known existential failure arm
        // is exactly the Lottie shape; must be dropped so it never emits broken outbound marshalling.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError());
        Assert.True(handler.HasNonSwiftObjectGenericArg(spec, isParameterPosition: true));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ResultAnyErrorReturn_Admitted()
    {
        // The SAME Result<String, any Error> in RETURN position stays admitted (resultBypassApplies):
        // Session 3's whole point is projecting Result in return/property position.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError());
        Assert.False(handler.HasNonSwiftObjectGenericArg(spec, isParameterPosition: false));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ResultClassSuccessParameter_Dropped()
    {
        // Result<LoadedAsset, any Error> param — bound-class success arm. The pre-existing per-arm
        // ISwiftObject loop would NOT catch this (a class arm is ISwiftObject and `any Error` is a
        // ProtocolListTypeSpec, not a NamedTypeSpec), so the explicit Result-param guard is what drops it.
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("TestModule.LoadedAsset"), AnyError());
        Assert.True(handler.HasNonSwiftObjectGenericArg(spec, isParameterPosition: true));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_ResultConcreteArmsParameter_Dropped()
    {
        // Result<String, Int> param — both arms are ordinary projectable value types (NO existential
        // arm at all). The return-only invariant must still hold: GetParameterPlan throws regardless
        // of arm shape, so the guard drops it (closes the latent concrete-arm hole, not just the
        // existential-arm regression).
        var db = BuildResultAdmissionTypeDatabase();
        var handler = new BoundGenericsHandler(db);

        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.Int"));
        Assert.True(handler.HasNonSwiftObjectGenericArg(spec, isParameterPosition: true));
    }

    // ContainsResultArgument is the accessor-path counterpart to the parameter-direction Result
    // drop above. Accessors (property setters, subscript index/newValue) bypass Gate 5, so the
    // property/subscript preflights call this NARROW structural Result detector — it must match a
    // Result anywhere in a write-in slot's type tree, yet must NOT match legitimate non-Result
    // existential containers (which the broad HasNonSwiftObjectGenericArg would over-drop).

    [Fact]
    public void ContainsResultArgument_TopLevelResult_True()
    {
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var spec = BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError());
        Assert.True(handler.ContainsResultArgument(spec));
    }

    [Fact]
    public void ContainsResultArgument_ArrayOfResult_True()
    {
        // [Result<String, any Error>] — a Result nested inside an Array still marshals a
        // C#-constructed SwiftResult outbound, so a setter/index carrying it must drop.
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError()));
        Assert.True(handler.ContainsResultArgument(array));
    }

    [Fact]
    public void ContainsResultArgument_OptionalOfResult_True()
    {
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError()));
        Assert.True(handler.ContainsResultArgument(optional));
    }

    [Fact]
    public void ContainsResultArgument_DictionaryValueResult_True()
    {
        // [String: Result<String, any Error>] — Result reachable through the dictionary value arm.
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var dict = new NamedTypeSpec("Swift.Dictionary");
        dict.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dict.GenericParameters.Add(BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError()));
        Assert.True(handler.ContainsResultArgument(dict));
    }

    [Fact]
    public void ContainsResultArgument_TupleWithResultElement_True()
    {
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            BuildResultOf(new NamedTypeSpec("Swift.String"), AnyError()),
        });
        Assert.True(handler.ContainsResultArgument(tuple));
    }

    [Fact]
    public void ContainsResultArgument_DictionaryOfAnySendable_False()
    {
        // [String: any Sendable] — the exact settable-property shape (SendableInfoBox.stringKeyed).
        // It carries NO Result, so the narrow detector must return false; its setter stays supported
        // (the broad non-ISwiftObject predicate would have wrongly dropped it).
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var dict = new NamedTypeSpec("Swift.Dictionary");
        dict.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dict.GenericParameters.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") }));
        Assert.False(handler.ContainsResultArgument(dict));
    }

    [Fact]
    public void ContainsResultArgument_ArrayOfExistential_False()
    {
        // [any Describable] — a non-Result existential array (DescribableBag.contents shape).
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Describable") }));
        Assert.False(handler.ContainsResultArgument(array));
    }

    [Fact]
    public void ContainsResultArgument_PlainValueType_False()
    {
        var handler = new BoundGenericsHandler(BuildResultAdmissionTypeDatabase());
        Assert.False(handler.ContainsResultArgument(new NamedTypeSpec("Swift.String")));
    }

    #endregion
}
