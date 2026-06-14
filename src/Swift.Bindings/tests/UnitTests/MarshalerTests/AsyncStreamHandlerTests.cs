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
    public void IsAsyncStream_WithAsyncThrowingStream_ReturnsFalse()
    {
        // AsyncThrowingStream is NOT a supported AsyncStream: its terminal iteration error has no
        // representation across the channel bridge, so IsAsyncStream must not match it (it is
        // rejected via IsThrowingStream → SkipReason.UnsupportedThrowingAsyncStream). Matching it
        // here would let it flow into the supported-stream emission path and half-bind.
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncThrowingStream", new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.IsAsyncStream(typeSpec));
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

        Assert.Equal("IAsyncEnumerable<long>", result);
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

    // AsyncStream<[T]> surfaces as IAsyncEnumerable<IReadOnlyList<T>> at the
    // public API boundary instead of leaking SwiftArray<T> (the runtime helper container).
    [Fact]
    public void GetCSharpAsyncEnumerableType_WithArrayOfIntElement_ProjectsToReadOnlyList()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        // AsyncStream<[Int]>
        var element = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<IReadOnlyList<long>>", result);
    }

    [Fact]
    public void GetCSharpAsyncEnumerableType_WithSetOfIntElement_ProjectsToReadOnlySet()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        // AsyncStream<Set<Int>>
        var element = new NamedTypeSpec("Swift.Set", new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<IReadOnlySet<long>>", result);
    }

    [Fact]
    public void GetCSharpAsyncEnumerableType_WithDictionaryOfStringIntElement_ProjectsToReadOnlyDictionary()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        // AsyncStream<[String: Int]>
        var element = new NamedTypeSpec(
            "Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpAsyncEnumerableType(typeSpec);

        Assert.Equal("IAsyncEnumerable<IReadOnlyDictionary<Swift.SwiftString, long>>", result);
    }

    #endregion

    #region Boundary projection — channel storage type retains SwiftArray/SwiftSet/SwiftDictionary

    // The internal SwiftAsyncStream<T> channel storage type must retain the runtime
    // helper container (SwiftArray<T> etc.) because SwiftMarshal.MarshalFromSwift<T> in
    // the channel's OnElement deserializes the Swift payload into that exact runtime
    // container. The public boundary projection only applies to the consumer-facing
    // IAsyncEnumerable<T>; covariance closes the loop at the property getter return.
    [Fact]
    public void GetCSharpInternalChannelElementType_WithArrayOfInt_RetainsSwiftArray()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var element = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpInternalChannelElementType(typeSpec);

        Assert.Equal("Swift.SwiftArray<long>", result);
    }

    [Fact]
    public void GetCSharpInternalChannelElementType_WithSetOfInt_RetainsSwiftSet()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var element = new NamedTypeSpec("Swift.Set", new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpInternalChannelElementType(typeSpec);

        Assert.Equal("Swift.SwiftSet<long>", result);
    }

    [Fact]
    public void GetCSharpInternalChannelElementType_WithDictionaryOfStringInt_RetainsSwiftDictionary()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var element = new NamedTypeSpec(
            "Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpInternalChannelElementType(typeSpec);

        Assert.Equal("Swift.SwiftDictionary<Swift.SwiftString, long>", result);
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

        Assert.Equal("long", result);
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                // Needed for boundary projection / channel-storage tests of
                // AsyncStream<[T]>, AsyncStream<Set<T>>, AsyncStream<[K: V]>.
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Set"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftSet"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Set"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Dictionary"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
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

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
