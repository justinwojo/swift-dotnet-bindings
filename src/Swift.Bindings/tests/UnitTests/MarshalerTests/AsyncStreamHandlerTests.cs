// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStream type detection and handling.
/// </summary>
public class AsyncStreamHandlerTests
{
    /// <summary>
    /// The channel spells the element argument by argument, and an argument it cannot name (a tuple)
    /// becomes the Swift.AnyType placeholder: <c>AsyncStream&lt;[(Int32, Int32)]&gt;</c> has an Array
    /// record but streamed as <c>IAsyncEnumerable&lt;IReadOnlyList&lt;Swift.AnyType&gt;&gt;</c>, whose
    /// elements no consumer can read. Such a stream is not supported; an array of a known element is.
    /// </summary>
    [Theory]
    [InlineData("_Concurrency.AsyncStream", true, false)]
    [InlineData("_Concurrency.AsyncThrowingStream", true, false)]
    [InlineData("_Concurrency.AsyncStream", false, true)]
    public async Task IsSupportedAsyncStream_ElementTheChannelCannotSpell_IsNotSupported(string stream, bool tupleElement, bool supported)
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml"));
        var handler = new AsyncStreamHandler(database);
        TypeSpec element = tupleElement
            ? new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32") })
            : new NamedTypeSpec("Swift.Int32");
        var spec = new NamedTypeSpec(stream, new NamedTypeSpec("Swift.Array", element));

        Assert.Equal(supported, handler.IsSupportedAsyncStream(spec));
        if (supported)
            Assert.DoesNotContain("AnyType", handler.GetCSharpAsyncEnumerableType(spec));
    }

    /// <summary>
    /// An unresolved generic is rendered as the placeholder with its arguments appended
    /// (<c>Swift.AnyType&lt;int&gt;</c> for a metatype of <c>[Int32]</c>), which names no type C# has, so
    /// such a stream stays unsupported.
    /// </summary>
    [Fact]
    public async Task IsSupportedAsyncStream_UnresolvedGenericMetatypeElement_IsNotSupported()
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml"));
        var handler = new AsyncStreamHandler(database);
        var metatype = new NamedTypeSpec("Swift.Array.Type", new NamedTypeSpec("Swift.Int32"));
        var spec = new NamedTypeSpec("_Concurrency.AsyncStream", metatype);

        Assert.False(handler.IsSupportedAsyncStream(spec), handler.GetCSharpInternalChannelElementType(spec));
    }

    /// <summary>
    /// The placeholder is matched as a whole type name: a resolved element whose C# name starts with it
    /// (a <c>Swift.AnyTypePayload</c> under a <c>Swift</c> namespace pattern) streams as itself.
    /// </summary>
    [Fact]
    public async Task IsSupportedAsyncStream_ResolvedElementNamedLikeThePlaceholder_IsSupported()
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml"));
        var kit = new ModuleTypeDatabase("Kit", "/tmp/Kit.dylib");
        kit.RegisterType(SwiftTypeName.FromModuleQualifiedName("Kit.AnyTypePayload"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "AnyTypePayload"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Kit.AnyTypePayload"),
            MetadataAccessor = "$s3Kit14AnyTypePayloadVMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct,
        });
        database.AddModuleDatabase(kit);
        var handler = new AsyncStreamHandler(database);

        var spec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Kit.AnyTypePayload"));

        Assert.True(handler.IsSupportedAsyncStream(spec), handler.GetCSharpInternalChannelElementType(spec));
    }

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
        // AsyncThrowingStream IS now a supported async stream: it projects to IAsyncEnumerable<T>
        // exactly like AsyncStream (element = generic param [0] for both), and its
        // finish(throwing:) termination is marshalled through a producer-error callback that faults
        // the channel so the consumer's await foreach rethrows. IsAsyncStream therefore matches it
        // so it flows into the (now-supported) stream emission path. (Inverse of the Session-2
        // rejection — see IsThrowingStream below, which still distinguishes the throwing variant so
        // the emitter knows to wire the extra producer-error callback.)
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

        Assert.Equal("IAsyncEnumerable<nint>", result);
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

        Assert.Equal("IAsyncEnumerable<IReadOnlyList<nint>>", result);
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

        Assert.Equal("IAsyncEnumerable<IReadOnlySet<nint>>", result);
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

        Assert.Equal("IAsyncEnumerable<IReadOnlyDictionary<Swift.SwiftString, nint>>", result);
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

        Assert.Equal("Swift.SwiftArray<nint>", result);
    }

    [Fact]
    public void GetCSharpInternalChannelElementType_WithSetOfInt_RetainsSwiftSet()
    {
        var typeDatabase = new MockTypeDatabase();
        var handler = new AsyncStreamHandler(typeDatabase);

        var element = new NamedTypeSpec("Swift.Set", new NamedTypeSpec("Swift.Int"));
        var typeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", element);

        var result = handler.GetCSharpInternalChannelElementType(typeSpec);

        Assert.Equal("Swift.SwiftSet<nint>", result);
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

        Assert.Equal("Swift.SwiftDictionary<Swift.SwiftString, nint>", result);
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

    #region Opaque AsyncSequence Tests

    [Theory]
    [InlineData("Swift.Never", false)]
    [InlineData("Swift.Error", true)]
    public void OpaqueAsyncSequence_IsAStreamOfItsElement_ThrowingUnlessFailureIsNever(string failure, bool throwing)
    {
        var handler = new AsyncStreamHandler(new MockTypeDatabase());
        var typeSpec = OpaqueSequence(
            new NamedTypeSpec("_Concurrency.AsyncSequence", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec(failure) { IsAny = failure != "Swift.Never" }),
            "Swift.Sendable");

        Assert.True(handler.IsAsyncStream(typeSpec));
        Assert.True(handler.IsSupportedAsyncStream(typeSpec));
        Assert.Equal("Swift.Int", Assert.IsType<NamedTypeSpec>(handler.GetElementType(typeSpec)).Name);
        Assert.Equal(throwing, handler.IsThrowingStream(typeSpec));
    }

    public static TheoryData<string, TypeSpec> NotAnOpaqueStream => new()
    {
        // `any AsyncSequence<…>` is an existential the caller holds, not a sequence to iterate here.
        { "existential", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("_Concurrency.AsyncSequence") })
            { OpaqueParameterizedConstraints = new[] { IntSequence() } } },
        // Without its arguments the interface promises no element type.
        { "no arguments", OpaqueSequence(null, "Swift.Sendable") },
        // A protocol with requirements promises more than the bridge carries.
        { "extra protocol", OpaqueSequence(IntSequence(), "Swift.CustomStringConvertible") },
        // A Sequence is not iterated with await.
        { "sequence", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sequence") }) { IsOpaque = true,
            OpaqueParameterizedConstraints = new[] { new NamedTypeSpec("Swift.Sequence", new NamedTypeSpec("Swift.Int")) } } },
    };

    [Theory]
    [MemberData(nameof(NotAnOpaqueStream))]
    public void OpaqueType_OtherThanAPlainAsyncSequence_IsNotAStream(string shape, TypeSpec typeSpec)
    {
        var handler = new AsyncStreamHandler(new MockTypeDatabase());

        Assert.False(handler.IsAsyncStream(typeSpec), shape);
        Assert.Null(handler.GetElementType(typeSpec));
    }

    // The stream-property gates see an opaque sequence's element as they see AsyncStream<Element>'s
    // generic argument. The Failure is not marshalled (the bridge stringifies the error), so an
    // existential Failure such as `any Error` gates nothing. The general walk, which also reads
    // method signatures that box the sequence, leaves the constraint alone.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OpaqueAsyncSequence_ParameterizedProtocolGate_SeesTheElementOnly(bool existentialElement, bool expectedFree)
    {
        var existential = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Marker") });
        var typeSpec = OpaqueSequence(existentialElement
            ? new NamedTypeSpec("_Concurrency.AsyncSequence", existential, new NamedTypeSpec("Swift.Never"))
            : new NamedTypeSpec("_Concurrency.AsyncSequence", new NamedTypeSpec("Swift.Int"),
                new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") })));
        var typeDatabase = new MockTypeDatabase();

        Assert.Equal(!expectedFree, WrapperValidation.StreamContainsParameterizedProtocol(typeSpec, typeDatabase));
        Assert.False(WrapperValidation.ContainsParameterizedProtocol(typeSpec, typeDatabase));
    }

    [Fact]
    public void OpaqueAsyncSequence_WithAnUnsupportedElement_IsNotASupportedStream()
    {
        var handler = new AsyncStreamHandler(new MockTypeDatabase());
        var typeSpec = OpaqueSequence(new NamedTypeSpec("_Concurrency.AsyncSequence",
            new NamedTypeSpec("Foundation.Predicate"), new NamedTypeSpec("Swift.Never")));

        Assert.True(handler.IsAsyncStream(typeSpec));
        Assert.False(handler.IsSupportedAsyncStream(typeSpec));
        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.None,
            ValidationRuleSet.ClassifyUnsupportedReference(typeSpec, typeDatabase: null, out _));
    }

    private static NamedTypeSpec IntSequence()
        => new("_Concurrency.AsyncSequence", new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Never"));

    private static ProtocolListTypeSpec OpaqueSequence(NamedTypeSpec constraint, params string[] others)
    {
        var participants = others.Select(o => new NamedTypeSpec(o)).Append(new NamedTypeSpec("_Concurrency.AsyncSequence"));
        return new ProtocolListTypeSpec(participants)
        {
            IsOpaque = true,
            OpaqueParameterizedConstraints = constraint is null ? [] : new[] { constraint },
        };
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

        Assert.Equal("nint", result);
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
                    CSharpTypeName = CSharpTypeName.NIntType,
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
