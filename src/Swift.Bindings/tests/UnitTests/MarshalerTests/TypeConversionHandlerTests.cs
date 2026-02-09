// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the TypeConversionHandler which handles automatic .NET type conversions.
/// Converts Swift wrapper types (SwiftString, SwiftArray, SwiftOptional) to idiomatic .NET types.
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
    public void IsConvertibleType_SwiftSet_ReturnsFalse()
    {
        // SwiftSet is not yet supported for type conversion
        var typeSpec = new NamedTypeSpec("Swift.Set");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.False(_handler.IsConvertibleType(typeSpec));
    }

    #endregion

    #region GetIdiomaticCSharpType Tests

    [Fact]
    public void GetIdiomaticCSharpType_SwiftString_ReturnsString()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: true);
        Assert.Equal("string", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_SwiftString_ReturnType_ReturnsString()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: false);
        Assert.Equal("string", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_SwiftArrayParameter_ReturnsIEnumerable()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: true, _ => "long");
        Assert.Equal("IEnumerable<long>", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_SwiftArrayReturn_ReturnsIReadOnlyList()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: false, _ => "long");
        Assert.Equal("IReadOnlyList<long>", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_SwiftOptional_ReturnsNullable()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: true, _ => "long");
        Assert.Equal("long?", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_NonConvertibleType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: true);
        Assert.Null(result);
    }

    #endregion

    #region GetParameterConversion Tests

    [Fact]
    public void GetParameterConversion_SwiftString_ReturnsNewSwiftString()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _handler.GetParameterConversion("name", typeSpec);
        Assert.Equal("new SwiftString(name)", result);
    }

    [Fact]
    public void GetParameterConversion_SwiftArray_ReturnsFromEnumerable()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetParameterConversion("items", typeSpec, _ => "long");
        Assert.Equal("SwiftArray<long>.FromEnumerable(items)", result);
    }

    [Fact]
    public void GetParameterConversion_SwiftOptional_ReturnsPatternMatch()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetParameterConversion("value", typeSpec, _ => "long");
        Assert.Equal("(value is {} valueVal ? SwiftOptional<long>.NewSome(valueVal) : SwiftOptional<long>.NewNone())", result);
    }

    [Fact]
    public void GetParameterConversion_NonConvertibleType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var result = _handler.GetParameterConversion("value", typeSpec);
        Assert.Null(result);
    }

    #endregion

    #region GetReturnConversion Tests

    [Fact]
    public void GetReturnConversion_SwiftString_ReturnsToString()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _handler.GetReturnConversion("result", typeSpec);
        Assert.Equal("result.ToString()", result);
    }

    [Fact]
    public void GetReturnConversion_SwiftArray_ReturnsDirectly()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetReturnConversion("result", typeSpec);
        Assert.Equal("result", result);
    }

    [Fact]
    public void GetReturnConversion_SwiftOptional_ReturnsCast()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetReturnConversion("result", typeSpec, _ => "long");
        Assert.Equal("((long?)result)", result);
    }

    [Fact]
    public void GetReturnConversion_NonConvertibleType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var result = _handler.GetReturnConversion("result", typeSpec);
        Assert.Null(result);
    }

    #endregion

    #region GetSwiftWrapperType Tests

    [Fact]
    public void GetSwiftWrapperType_SwiftString_ReturnsSwiftString()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _handler.GetSwiftWrapperType(typeSpec);
        Assert.Equal("SwiftString", result);
    }

    [Fact]
    public void GetSwiftWrapperType_SwiftArray_ReturnsSwiftArrayWithElement()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetSwiftWrapperType(typeSpec, _ => "long");
        Assert.Equal("SwiftArray<long>", result);
    }

    [Fact]
    public void GetSwiftWrapperType_SwiftOptional_ReturnsSwiftOptionalWithElement()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetSwiftWrapperType(typeSpec, _ => "long");
        Assert.Equal("SwiftOptional<long>", result);
    }

    [Fact]
    public void GetSwiftWrapperType_NonConvertibleType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var result = _handler.GetSwiftWrapperType(typeSpec);
        Assert.Null(result);
    }

    #endregion

    #region GetElementType Tests

    [Fact]
    public void GetElementType_WithGenericParameter_ReturnsElementType()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var result = _handler.GetElementType(typeSpec, _ => "long");
        Assert.Equal("long", result);
    }

    [Fact]
    public void GetElementType_NoGenericParameters_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array");
        var result = _handler.GetElementType(typeSpec);
        Assert.Null(result);
    }

    #endregion

    #region TypeTranslator Regression Tests (Property Getter/Setter Conversion)

    [Fact]
    public void GetParameterConversion_SwiftArray_WithTranslator_UsesTranslatedElementType()
    {
        // Regression test: PropertyHandler.EmitSetter must pass a typeTranslator to
        // GetParameterConversion so that element types are correctly resolved.
        // Without the translator, unregistered element types fall back to AnyType.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        // With translator: element type resolves to "System.Byte"
        var result = _handler.GetParameterConversion("value", typeSpec, _ => "System.Byte");
        Assert.Equal("SwiftArray<System.Byte>.FromEnumerable(value)", result);
    }

    [Fact]
    public void GetParameterConversion_SwiftArray_WithoutTranslator_FallsBackToDbLookup()
    {
        // Without a translator, the element type is looked up in the TypeDatabase.
        // For unregistered types (Swift.UInt8 is not in the mock), this falls back to AnyType.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        var result = _handler.GetParameterConversion("value", typeSpec);
        // AnyType fallback produces a different (incorrect) element type
        Assert.NotNull(result);
        Assert.DoesNotContain("System.Byte", result);
    }

    [Fact]
    public void GetSwiftWrapperType_SwiftArray_WithTranslator_UsesTranslatedElementType()
    {
        // Regression test: PropertyHandler property type declaration must pass a typeTranslator
        // so that SwiftArray<T> gets the correct element type.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        var result = _handler.GetSwiftWrapperType(typeSpec, _ => "System.Byte");
        Assert.Equal("SwiftArray<System.Byte>", result);
    }

    [Fact]
    public void GetSwiftWrapperType_SwiftArray_WithoutTranslator_FallsBackToDbLookup()
    {
        // Without translator, unregistered element types produce incorrect wrapper types.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        var result = _handler.GetSwiftWrapperType(typeSpec);
        Assert.NotNull(result);
        Assert.DoesNotContain("System.Byte", result);
    }

    [Fact]
    public void GetIdiomaticCSharpType_SwiftArray_WithTranslator_ProducesCorrectGenericType()
    {
        // Regression test: Property type declaration uses GetIdiomaticCSharpType with translator.
        // The translator ensures the correct element type appears in IReadOnlyList<T>.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        var result = _handler.GetIdiomaticCSharpType(typeSpec, isParameter: false, _ => "System.Byte");
        Assert.Equal("IReadOnlyList<System.Byte>", result);
    }

    [Fact]
    public void GetParameterConversion_SwiftArray_TranslatorOverridesDbLookup()
    {
        // Even when the element type IS registered in the DB, the translator takes precedence.
        // This ensures consistency between property type declarations and getter/setter conversions.
        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        // DB has Swift.Int → System.Int64, but translator returns "nint" (hypothetical override)
        var result = _handler.GetParameterConversion("items", typeSpec, _ => "nint");
        Assert.Equal("SwiftArray<nint>.FromEnumerable(items)", result);
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
