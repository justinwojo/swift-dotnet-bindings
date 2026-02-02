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
    public void TranslateBoundGenericTypeToCSharp_DictionaryWithAny_FallsBackToAnyType()
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
    public void TranslateBoundGenericTypeToCSharp_ArrayWithAny_FallsBackToAnyType()
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
    public void TranslateBoundGenericTypeToCSharp_OptionalWithAny_FallsBackToAnyType()
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
    public void TranslateBoundGenericTypeToCSharp_NestedArrayOfDictionaryWithAny_FallsBackToAnyType()
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
        Assert.Contains("AnyType", result);
    }

    #endregion

    #region Property Bound Generic Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_Property_WithAny_FallsBackToAnyType()
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
