// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Optional type detection and handling.
/// These tests focus on the Swift.Optional type being properly recognized and translated to C#.
/// </summary>
public class OptionalHandlerTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly BoundGenericsHandler _boundGenericsHandler;

    public OptionalHandlerTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
    }

    #region Type Database Registration Tests

    [Fact]
    public void TryGetTypeRecord_OptionalType_ReturnsTrue()
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");

        var found = _typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal("SwiftOptional", record!.CSharpTypeName.Name);
        Assert.Equal("Swift", record.CSharpTypeName.Namespace);
    }

    [Fact]
    public void IsTypeProcessed_OptionalType_ReturnsTrue()
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");

        var result = _typeDatabase.IsTypeProcessed(swiftTypeName);

        Assert.True(result);
    }

    #endregion

    #region Bound Generic Detection Tests

    [Fact]
    public void IsBoundGeneric_PropertyWithOptionalInt_ReturnsTrue()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var property = CreatePropertyDecl("value", optionalInt);

        var result = _boundGenericsHandler.IsBoundGeneric(property);

        Assert.True(result);
    }

    [Fact]
    public void IsBoundGeneric_PropertyWithNonGenericType_ReturnsFalse()
    {
        var intType = new NamedTypeSpec("Swift.Int");
        var property = CreatePropertyDecl("count", intType);

        var result = _boundGenericsHandler.IsBoundGeneric(property);

        Assert.False(result);
    }

    [Fact]
    public void IsBoundGeneric_ArgumentWithOptionalString_ReturnsTrue()
    {
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var argument = CreateArgumentDecl("name", optionalString);

        var result = _boundGenericsHandler.IsBoundGeneric(argument);

        Assert.True(result);
    }

    #endregion

    #region C# Type Translation Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalInt_ReturnsSwiftOptionalInt()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var property = CreatePropertyDecl("value", optionalInt);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<System.Int64>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalString_ReturnsSwiftOptionalString()
    {
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var property = CreatePropertyDecl("name", optionalString);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<Swift.SwiftString>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalBool_ReturnsSwiftOptionalBool()
    {
        var optionalBool = new NamedTypeSpec("Swift.Optional");
        optionalBool.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));
        var property = CreatePropertyDecl("isEnabled", optionalBool);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<System.Boolean>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalDouble_ReturnsSwiftOptionalDouble()
    {
        var optionalDouble = new NamedTypeSpec("Swift.Optional");
        optionalDouble.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        var property = CreatePropertyDecl("price", optionalDouble);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<System.Double>", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_ArgumentOptionalInt_ReturnsSwiftOptionalInt()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var argument = CreateArgumentDecl("count", optionalInt);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument);

        Assert.Equal("Swift.SwiftOptional<System.Int64>", result);
    }

    #endregion

    #region Nested Optional Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_NestedOptional_ReturnsNestedSwiftOptional()
    {
        // Swift: Optional<Optional<Int>> = Int??
        var innerOptional = new NamedTypeSpec("Swift.Optional");
        innerOptional.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var outerOptional = new NamedTypeSpec("Swift.Optional");
        outerOptional.GenericParameters.Add(innerOptional);

        var property = CreatePropertyDecl("maybeValue", outerOptional);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<Swift.SwiftOptional<System.Int64>>", result);
    }

    #endregion

    #region Optional with Complex Inner Types Tests

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalArray_ReturnsSwiftOptionalArray()
    {
        // Swift: Optional<Array<Int>> = [Int]?
        var arrayInt = new NamedTypeSpec("Swift.Array");
        arrayInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var optionalArray = new NamedTypeSpec("Swift.Optional");
        optionalArray.GenericParameters.Add(arrayInt);

        var property = CreatePropertyDecl("items", optionalArray);

        var result = _boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);

        Assert.Equal("Swift.SwiftOptional<Swift.SwiftArray<System.Int64>>", result);
    }

    #endregion

    #region Helper Methods

    private static PropertyDecl CreatePropertyDecl(string name, TypeSpec typeSpec)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region Mock Type Database

    /// <summary>
    /// Mock type database that includes Optional type registration for testing.
    /// </summary>
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
                    MetadataAccessor = "$sSiMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "$sSbMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Double"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                    MetadataAccessor = "$sSdMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "$sSSMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "$sSaMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // The key addition: Swift.Optional mapping to SwiftOptional
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "$sSqMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
