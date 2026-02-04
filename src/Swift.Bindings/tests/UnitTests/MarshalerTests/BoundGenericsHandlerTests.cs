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
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithAny_ResolvesToExistentialContainer()
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
        Assert.Contains("ExistentialContainer0", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArrayWithAny_ResolvesToExistentialContainer()
    {
        // Swift: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("ExistentialContainer0", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalWithAny_ResolvesToExistentialContainer()
    {
        // Swift: Optional<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(anyTypeSpec);

        var argDecl = CreateArgumentDecl(optionalTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftOptional", result);
        Assert.Contains("ExistentialContainer0", result);
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
        Assert.Contains("ExistentialContainer1", result);
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
        Assert.Contains("ExistentialContainer2", result);
    }

    #endregion

    #region Nested Generic with Existential Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NestedArrayOfDictionaryWithAny_ResolvesToExistentialContainer()
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
        Assert.Contains("Int64", result); // Int maps to Int64
        Assert.Contains("ExistentialContainer0", result);
    }

    #endregion

    #region Property Bound Generic Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_Property_WithAny_ResolvesToExistentialContainer()
    {
        // Swift property: var items: Array<Any>
        var anyTypeSpec = new ProtocolListTypeSpec();
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(anyTypeSpec);

        var propertyDecl = CreatePropertyDecl(arrayTypeSpec);
        var result = _handler.TranslateBoundGenericTypeToCSharp(propertyDecl);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("ExistentialContainer0", result);
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
    }

    #endregion
}
