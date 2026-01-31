// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStream type detection and handling.
/// </summary>
public class AsyncStreamHandlerTests
{
    #region AsyncStream Detection Tests

    [Fact]
    public void IsAsyncStream_WithAsyncStream_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));

        Assert.True(handler.IsAsyncStream(typeSpec));
    }

    [Fact]
    public void IsAsyncStream_WithAsyncThrowingStream_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncThrowingStream", new NamedTypeSpec("Swift.Int"));

        Assert.True(handler.IsAsyncStream(typeSpec));
    }

    [Fact]
    public void IsAsyncStream_WithNonAsyncStream_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsAsyncStream(typeSpec));
    }

    [Fact]
    public void IsAsyncStream_WithNonNamedType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = TupleTypeSpec.Empty;

        Assert.False(handler.IsAsyncStream(typeSpec));
    }

    #endregion

    #region Element Type Extraction Tests

    [Fact]
    public void GetElementType_WithAsyncStream_ReturnsElementType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var elementType = new NamedTypeSpec("Swift.Int");
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", elementType);

        var result = handler.GetElementType(typeSpec);

        Assert.NotNull(result);
        Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Int", ((NamedTypeSpec)result!).Name);
    }

    [Fact]
    public void GetElementType_WithNoGenericParams_ReturnsNull()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream");

        var result = handler.GetElementType(typeSpec);

        Assert.Null(result);
    }

    [Fact]
    public void GetElementType_WithNonAsyncStream_ReturnsNull()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var result = handler.GetElementType(typeSpec);

        Assert.Null(result);
    }

    #endregion

    #region Support Check Tests

    [Fact]
    public void IsSupportedAsyncStream_WithKnownElementType_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));

        Assert.True(handler.IsSupportedAsyncStream(typeSpec));
    }

    [Fact]
    public void IsSupportedAsyncStream_WithUnknownElementType_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("SomeModule.UnknownType"));

        Assert.False(handler.IsSupportedAsyncStream(typeSpec));
    }

    [Fact]
    public void IsSupportedAsyncStream_WithNonAsyncStream_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsSupportedAsyncStream(typeSpec));
    }

    #endregion

    #region C# Type Generation Tests

    [Fact]
    public void GetCSharpAsyncEnumerableType_WithIntElement_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<System.Int64>", result);
    }

    [Fact]
    public void GetCSharpAsyncEnumerableType_WithStringElement_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.String"));

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<Swift.SwiftString>", result);
    }

    [Fact]
    public void GetCSharpAsyncEnumerableType_WithNoElementType_ReturnsObjectFallback()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream");

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<object>", result);
    }

    #endregion

    #region Throwing Stream Tests

    [Fact]
    public void IsThrowingStream_WithAsyncThrowingStream_ReturnsTrue()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncThrowingStream", new NamedTypeSpec("Swift.Int"));

        Assert.True(handler.IsThrowingStream(typeSpec));
    }

    [Fact]
    public void IsThrowingStream_WithAsyncStream_ReturnsFalse()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsThrowingStream(typeSpec));
    }

    #endregion

    #region C# Element Type Tests

    [Fact]
    public void GetCSharpElementType_WithKnownType_ReturnsCorrectType()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));

        var result = handler.GetCSharpElementType(typeSpec);

        Assert.Equal("System.Int64", result);
    }

    [Fact]
    public void GetCSharpElementType_WithNoElementType_ReturnsObject()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream");

        var result = handler.GetCSharpElementType(typeSpec);

        Assert.Equal("object", result);
    }

    #endregion

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";
    }

    #endregion
}
