// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the TypeConversionHandler which handles type detection, disposal helpers,
/// and native type remapping (URL → NSUrl, Data → NSData).
/// </summary>
public class TypeConversionHandlerTests
{
    private readonly TypeConversionHandler _handler;
    private readonly MockTypeDatabase _mockDatabase;

    public TypeConversionHandlerTests()
    {
        _mockDatabase = new MockTypeDatabase();
        _handler = new TypeConversionHandler(_mockDatabase);
    }

    #region IsSwiftString Tests

    [Fact]
    public void IsSwiftString_SwiftString_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        Assert.True(_handler.IsSwiftString(typeSpec));
    }

    [Fact]
    public void IsSwiftString_OtherType_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        Assert.False(_handler.IsSwiftString(typeSpec));
    }

    [Fact]
    public void IsSwiftString_Null_ReturnsFalse()
    {
        Assert.False(_handler.IsSwiftString(null));
    }

    [Fact]
    public void IsSwiftString_NonNamedTypeSpec_ReturnsFalse()
    {
        var typeSpec = TupleTypeSpec.Empty;
        Assert.False(_handler.IsSwiftString(typeSpec));
    }

    #endregion

    #region IsSwiftArray Tests

    [Fact]
    public void IsSwiftArray_SwiftArray_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(_handler.IsSwiftArray(typeSpec));
    }

    [Fact]
    public void IsSwiftArray_OtherType_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Set");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.False(_handler.IsSwiftArray(typeSpec));
    }

    [Fact]
    public void IsSwiftArray_Null_ReturnsFalse()
    {
        Assert.False(_handler.IsSwiftArray(null));
    }

    #endregion

    #region IsSwiftOptional Tests

    [Fact]
    public void IsSwiftOptional_SwiftOptional_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(_handler.IsSwiftOptional(typeSpec));
    }

    [Fact]
    public void IsSwiftOptional_OtherType_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        Assert.False(_handler.IsSwiftOptional(typeSpec));
    }

    [Fact]
    public void IsSwiftOptional_Null_ReturnsFalse()
    {
        Assert.False(_handler.IsSwiftOptional(null));
    }

    #endregion

    #region IsConvertibleType Tests

    [Fact]
    public void IsConvertibleType_SwiftString_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        Assert.True(_handler.IsConvertibleType(typeSpec));
    }

    [Fact]
    public void IsConvertibleType_SwiftArray_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(_handler.IsConvertibleType(typeSpec));
    }

    [Fact]
    public void IsConvertibleType_SwiftOptional_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(_handler.IsConvertibleType(typeSpec));
    }

    [Fact]
    public void IsConvertibleType_OtherType_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        Assert.False(_handler.IsConvertibleType(typeSpec));
    }

    [Fact]
    public void IsConvertibleType_SwiftSet_ReturnsTrue()
    {
        // SwiftSet is supported for type conversion (IReadOnlySet<T>)
        var typeSpec = new NamedTypeSpec("Swift.Set");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(_handler.IsConvertibleType(typeSpec));
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
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Enum
                }
            };
        }

        public string GetLibraryPath(string moduleName) => $"lib{moduleName}.dylib";

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            if (_types.TryGetValue(swiftTypeName.ModuleQualifiedName, out var typeRecord))
            {
                record = typeRecord;
                return true;
            }
            record = null;
            return false;
        }

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
