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
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithAny_ResolvesToAnyType()
    {
        // Swift: Dictionary<String, Any>
        // The 'Any' type is represented as a ProtocolListTypeSpec with 0 protocols
        var anyTypeSpec = new ProtocolListTypeSpec(); // Empty protocol list = Any
        var keyTypeSpec = new NamedTypeSpec("Swift.String");
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(keyTypeSpec);
        dictTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(dictTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftDictionary", result);
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayWithAny_ResolvesToAnyType()
    {
        // Swift: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalWithAny_ResolvesToAnyType()
    {
        // Swift: Optional<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(optionalTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftOptional", result);
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_WithSingleProtocolExistential_ResolvesToExistentialContainer()
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
    public void TranslateBoundGenericTypeToCSharp_WithTwoProtocolExistential_ResolvesToExistentialContainer()
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
    public void TranslateBoundGenericTypeToCSharp_NestedArrayOfDictionaryWithAny_ResolvesToAnyType()
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
        Assert.Contains("AnyType", result);
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
        CreateStructDecl("LottieVector3D", moduleDecl);

        var boundGeneric = new NamedTypeSpec("TestModule.ValueProviderStorage", new NamedTypeSpec("TestModule.LottieVector3D"));
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
        Assert.Contains("long", result); // Int maps to long (keyword alias)
        Assert.Contains("AnyType", result);
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
    public void TranslateBoundGenericTypeToCSharp_Property_WithAny_ResolvesToAnyType()
    {
        // Swift property: var items: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var propertyDecl = CreatePropertyDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(propertyDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("AnyType", result);
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

    [Fact]
    public void IsBareGenericUsage_StdlibGenericWithoutArgs_ReturnsTrue()
    {
        var isBare = _handler.IsBareGenericUsage(new NamedTypeSpec("Swift.Dictionary"), moduleDecl: null);
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
    public void HasNonSwiftObjectGenericArg_WithObjCBridgedArg_ReturnsTrue()
    {
        var generic = new NamedTypeSpec("TestModule.Future");
        generic.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        Assert.True(_handler.HasNonSwiftObjectGenericArg(generic));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_WithNestedObjCBridgedArg_ReturnsTrue()
    {
        // ObjC-bridged nested inside another generic is still caught
        var inner = new NamedTypeSpec("Swift.Optional");
        inner.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));

        var outer = new NamedTypeSpec("TestModule.Container");
        outer.GenericParameters.Add(inner);

        Assert.True(_handler.HasNonSwiftObjectGenericArg(outer));
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

        Assert.Equal("Swift.TestModule.Outer.Inner<T0, T1>.Leaf<T0, T1>", result);
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Outer.Inner.Leaf"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner.Leaf"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.Container.Item"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Container.Item"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container.Item"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.Box"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Box"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
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
}
