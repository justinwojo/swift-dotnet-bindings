// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the ExistentialHandler class.
/// </summary>
public class ExistentialHandlerTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly ExistentialHandler _handler;

    public ExistentialHandlerTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _handler = new ExistentialHandler(_typeDatabase);
    }

    #region IsExistential Tests

    [Fact]
    public void IsExistential_WithProtocolListTypeSpec_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec();
        Assert.True(_handler.IsExistential(protocolList));
    }

    [Fact]
    public void IsExistential_WithNamedTypeSpec_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        Assert.False(_handler.IsExistential(namedType));
    }

    [Fact]
    public void IsExistential_WithTupleTypeSpec_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") });
        Assert.False(_handler.IsExistential(tuple));
    }

    [Fact]
    public void IsExistential_WithClosureTypeSpec_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        Assert.False(_handler.IsExistential(closure));
    }

    [Fact]
    public void IsExistential_WithNamedTypeSpecIsAny_ReturnsTrue()
    {
        // Single protocol existential: "any SomeProtocol" is parsed as NamedTypeSpec with IsAny=true
        var namedType = new NamedTypeSpec("Swift.Equatable") { IsAny = true };
        Assert.True(_handler.IsExistential(namedType));
    }

    [Fact]
    public void IsExistential_WithNamedTypeSpecNotAny_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Equatable") { IsAny = false };
        Assert.False(_handler.IsExistential(namedType));
    }

    [Fact]
    public void IsExistential_ArgumentDecl_WithProtocolList_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var argDecl = CreateArgumentDecl(protocolList);

        Assert.True(_handler.IsExistential(argDecl));
    }

    [Fact]
    public void IsExistential_ArgumentDecl_WithNamedType_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argDecl = CreateArgumentDecl(namedType);

        Assert.False(_handler.IsExistential(argDecl));
    }

    #endregion

    #region GetProtocolListTypeSpec Tests

    [Fact]
    public void GetProtocolListTypeSpec_WithProtocolList_ReturnsProtocolList()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var argDecl = CreateArgumentDecl(protocolList);

        var result = _handler.GetProtocolListTypeSpec(argDecl);

        Assert.NotNull(result);
        Assert.Single(result!.Protocols);
    }

    [Fact]
    public void GetProtocolListTypeSpec_WithNamedType_ReturnsNull()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argDecl = CreateArgumentDecl(namedType);

        var result = _handler.GetProtocolListTypeSpec(argDecl);

        Assert.Null(result);
    }

    #endregion

    #region GetProtocolCount Tests

    [Fact]
    public void GetProtocolCount_WithEmptyProtocolList_ReturnsZero()
    {
        var protocolList = new ProtocolListTypeSpec();

        Assert.Equal(0, _handler.GetProtocolCount(protocolList));
    }

    [Fact]
    public void GetProtocolCount_WithSingleProtocol_ReturnsOne()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        Assert.Equal(1, _handler.GetProtocolCount(protocolList));
    }

    [Fact]
    public void GetProtocolCount_WithMultipleProtocols_ReturnsCorrectCount()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable"),
            new NamedTypeSpec("Swift.Comparable")
        });

        Assert.Equal(3, _handler.GetProtocolCount(protocolList));
    }

    #endregion

    #region IsAnyType Tests

    [Fact]
    public void IsAnyType_WithEmptyProtocolList_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec();

        Assert.True(_handler.IsAnyType(protocolList));
    }

    [Fact]
    public void IsAnyType_WithProtocols_ReturnsFalse()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        Assert.False(_handler.IsAnyType(protocolList));
    }

    #endregion

    #region IsSupportedExistential Tests

    [Fact]
    public void IsSupportedExistential_WithEmptyProtocolList_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec();

        Assert.True(_handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void IsSupportedExistential_WithOneProtocol_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        Assert.True(_handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void IsSupportedExistential_WithEightProtocols_ReturnsTrue()
    {
        var protocols = Enumerable.Range(1, 8)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.True(_handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void IsSupportedExistential_WithNineProtocols_ReturnsFalse()
    {
        var protocols = Enumerable.Range(1, 9)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.False(_handler.IsSupportedExistential(protocolList));
    }

    #endregion

    #region GetCSharpExistentialType Tests

    [Fact]
    public void GetCSharpExistentialType_WithZeroProtocols_ReturnsExistentialContainer0()
    {
        var protocolList = new ProtocolListTypeSpec();

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer0", result);
    }

    [Fact]
    public void GetCSharpExistentialType_WithOneProtocol_ReturnsExistentialContainer1()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    [Fact]
    public void GetCSharpExistentialType_WithFiveProtocols_ReturnsExistentialContainer5()
    {
        var protocols = Enumerable.Range(1, 5)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer5", result);
    }

    #endregion

    #region GetExistentialContainerSize Tests

    [Fact]
    public void GetExistentialContainerSizeInWords_WithZeroProtocols_Returns4()
    {
        var protocolList = new ProtocolListTypeSpec();

        // 3 payload + 1 metadata + 0 witness tables = 4 words
        Assert.Equal(4, _handler.GetExistentialContainerSizeInWords(protocolList));
    }

    [Fact]
    public void GetExistentialContainerSizeInWords_WithOneProtocol_Returns5()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // 3 payload + 1 metadata + 1 witness table = 5 words
        Assert.Equal(5, _handler.GetExistentialContainerSizeInWords(protocolList));
    }

    [Fact]
    public void GetExistentialContainerSizeInWords_WithThreeProtocols_Returns7()
    {
        var protocols = Enumerable.Range(1, 3)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        // 3 payload + 1 metadata + 3 witness tables = 7 words
        Assert.Equal(7, _handler.GetExistentialContainerSizeInWords(protocolList));
    }

    [Fact]
    public void GetExistentialContainerSizeInBytes_WithZeroProtocols_Returns32()
    {
        var protocolList = new ProtocolListTypeSpec();

        // 4 words * 8 bytes = 32 bytes
        Assert.Equal(32, _handler.GetExistentialContainerSizeInBytes(protocolList));
    }

    [Fact]
    public void GetExistentialContainerSizeInBytes_WithOneProtocol_Returns40()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // 5 words * 8 bytes = 40 bytes
        Assert.Equal(40, _handler.GetExistentialContainerSizeInBytes(protocolList));
    }

    #endregion

    #region GetExistentialDescription Tests

    [Fact]
    public void GetExistentialDescription_WithEmptyProtocolList_ReturnsAny()
    {
        var protocolList = new ProtocolListTypeSpec();

        var result = _handler.GetExistentialDescription(protocolList);

        Assert.Equal("Any", result);
    }

    [Fact]
    public void GetExistentialDescription_WithSingleProtocol_ReturnsAnyProtocol()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var result = _handler.GetExistentialDescription(protocolList);

        Assert.Equal("any Equatable", result);
    }

    [Fact]
    public void GetExistentialDescription_WithMultipleProtocols_ReturnsProtocolComposition()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });

        var result = _handler.GetExistentialDescription(protocolList);

        // Protocols are sorted alphabetically
        Assert.Contains("any", result);
        Assert.Contains("&", result);
    }

    #endregion

    #region GetProtocols Tests

    [Fact]
    public void GetProtocols_WithProtocolList_ReturnsAllProtocols()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });

        var result = _handler.GetProtocols(protocolList);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetProtocols_WithEmptyProtocolList_ReturnsEmptyList()
    {
        var protocolList = new ProtocolListTypeSpec();

        var result = _handler.GetProtocols(protocolList);

        Assert.Empty(result);
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

    #endregion

    #region TypeRecordKind Tests

    [Fact]
    public void GetExistentialTypeRecord_SetsKindToExistential()
    {
        // Use the TypeDatabaseExtensions to get a type record for an existential
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(protocolList);

        Assert.Equal(TypeRecordKind.Existential, typeRecord.Kind);
    }

    [Fact]
    public void GetExistentialTypeRecord_ForEmptyProtocolList_SetsKindToExistential()
    {
        var protocolList = new ProtocolListTypeSpec();

        var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(protocolList);

        Assert.Equal(TypeRecordKind.Existential, typeRecord.Kind);
    }

    [Fact]
    public void GetExistentialTypeRecord_HasFrozenFlag()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(protocolList);

        Assert.True(typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    [Fact]
    public void GetExistentialTypeRecord_MapsToCSharpExistentialContainer()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });

        var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer2", typeRecord.CSharpTypeName.FullyQualifiedName);
    }

    #endregion

    #region GetPInvokeExistentialType Tests

    [Fact]
    public void GetPInvokeExistentialType_MatchesCSharpType()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var csType = _handler.GetCSharpExistentialType(protocolList);
        var pinvokeType = _handler.GetPInvokeExistentialType(protocolList);

        Assert.Equal(csType, pinvokeType);
    }

    #endregion

    #region ToProtocolListTypeSpec Tests

    [Fact]
    public void ToProtocolListTypeSpec_WithProtocolList_ReturnsSameInstance()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var result = _handler.ToProtocolListTypeSpec(protocolList);

        Assert.Same(protocolList, result);
    }

    [Fact]
    public void ToProtocolListTypeSpec_WithNamedTypeSpecIsAny_CreatesProtocolList()
    {
        var namedType = new NamedTypeSpec("Swift.Equatable") { IsAny = true };

        var result = _handler.ToProtocolListTypeSpec(namedType);

        Assert.NotNull(result);
        Assert.Single(result!.Protocols);
        Assert.Equal("Swift.Equatable", result.Protocols.Keys.First().Name);
    }

    [Fact]
    public void ToProtocolListTypeSpec_WithNamedTypeSpecNotAny_ReturnsNull()
    {
        var namedType = new NamedTypeSpec("Swift.Equatable") { IsAny = false };

        var result = _handler.ToProtocolListTypeSpec(namedType);

        Assert.Null(result);
    }

    [Fact]
    public void ToProtocolListTypeSpec_WithTupleTypeSpec_ReturnsNull()
    {
        var tuple = new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") });

        var result = _handler.ToProtocolListTypeSpec(tuple);

        Assert.Null(result);
    }

    [Fact]
    public void ToProtocolListTypeSpec_SingleProtocolExistential_GeneratesExistentialContainer1()
    {
        var namedType = new NamedTypeSpec("Nuke.DataLoading") { IsAny = true };

        var protocolList = _handler.ToProtocolListTypeSpec(namedType);
        var csType = _handler.GetCSharpExistentialType(protocolList!);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", csType);
    }

    #endregion

    #region H2 Bug 4 — Optional Existential Container Type

    [Fact]
    public void UnwrapOptionalExistential_SingleProtocol_ReturnsProtocolList()
    {
        // H2 Bug 4: When an optional wraps an existential (e.g. Optional<any ImageDecoding>),
        // the marshal type must use ExistentialContainer1, not AnyType.
        // This test validates the handler correctly unwraps the optional existential.
        var optionalExistential = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Nuke.ImageDecoding") }));

        var protocolList = _handler.UnwrapOptionalExistential(optionalExistential);

        Assert.NotNull(protocolList);
        Assert.Single(protocolList!.Protocols);
        Assert.Equal("Swift.Runtime.ExistentialContainer1", _handler.GetCSharpExistentialType(protocolList));
    }

    [Fact]
    public void IsOptionalExistential_WithWrappedProtocolList_ReturnsTrue()
    {
        var optionalExistential = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Nuke.ImageDecoding") }));

        Assert.True(_handler.IsOptionalExistential(optionalExistential));
    }

    #endregion

    #region H2 Bug 5 — Existential Parameter Detection

    [Fact]
    public void IsExistential_ProtocolListTypeSpec_ReturnsTrue()
    {
        // H2 Bug 5: Async method filter must exclude existential parameters.
        // This test validates that IsExistential correctly identifies ProtocolListTypeSpec
        // (the existential type form used in method signatures like ILottieURLSession).
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Lottie.LottieURLSession") });

        Assert.True(_handler.IsExistential(protocolList));
    }

    [Fact]
    public void IsExistential_NamedTypeSpecWithIsAny_ReturnsTrue()
    {
        // Existential parameter expressed as NamedTypeSpec with IsAny=true
        var existentialType = new NamedTypeSpec("Lottie.LottieURLSession") { IsAny = true };

        Assert.True(_handler.IsExistential(existentialType));
    }

    [Fact]
    public void IsExistential_RegularNamedTypeSpec_ReturnsFalse()
    {
        // Non-existential types should not be filtered
        var regularType = new NamedTypeSpec("Swift.Int");

        Assert.False(_handler.IsExistential(regularType));
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
