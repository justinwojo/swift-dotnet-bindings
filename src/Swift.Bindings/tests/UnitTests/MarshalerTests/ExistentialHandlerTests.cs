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

    #region IsBareAny Tests

    [Fact]
    public void IsBareAny_EmptyProtocolList_ReturnsTrue()
    {
        // bare Any: 0 protocols
        var protocolList = new ProtocolListTypeSpec();
        Assert.True(_handler.IsBareAny(protocolList));
    }

    [Fact]
    public void IsBareAny_PureMarkerProtocol_ReturnsFalse()
    {
        // 'any Sendable' has 1 raw protocol — not bare Any (semantically distinct from 'Any')
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        Assert.False(_handler.IsBareAny(protocolList));
    }

    [Fact]
    public void IsBareAny_SingleProtocol_ReturnsFalse()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        Assert.False(_handler.IsBareAny(protocolList));
    }

    [Fact]
    public void IsBareAny_MultipleProtocols_ReturnsFalse()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        Assert.False(_handler.IsBareAny(protocolList));
    }

    #endregion

    #region IsZeroWitnessExistential Tests

    // IsZeroWitnessExistential is the container/projection-relevant notion of "bare Any":
    // an existential whose ABI container is the zero-witness-table ExistentialContainer0,
    // marshalled via Box/Unbox. It must be TRUE for bare Any AND any marker-only composition
    // (the two are distinct in Swift source but ABI-identical), and FALSE the moment any
    // witness-table-bearing protocol (a real Swift protocol OR an ObjC protocol) participates.

    [Fact]
    public void IsZeroWitnessExistential_BareAny_ReturnsTrue()
    {
        // bare Any: 0 protocols, 0 witness tables → Container0.
        Assert.True(ExistentialHandler.IsZeroWitnessExistential(new ProtocolListTypeSpec()));
    }

    [Fact]
    public void IsZeroWitnessExistential_PureMarker_ReturnsTrue()
    {
        // `any Sendable` filters to zero non-marker protocols → ABI-identical to bare Any.
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        Assert.True(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_MultipleMarkers_ReturnsTrue()
    {
        // `any Sendable & Copyable` — every participant is a marker → still zero-witness.
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("Swift.Copyable")
        });
        Assert.True(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_SingleRealProtocol_ReturnsFalse()
    {
        // `any Equatable` carries a witness table → Container1, not zero-witness.
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        Assert.False(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_MarkerPlusRealProtocol_ReturnsFalse()
    {
        // `any Sendable & Codable` — the marker is filtered but Codable's witness table remains.
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("Swift.Codable")
        });
        Assert.False(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_ObjCOnly_ReturnsFalse()
    {
        // An ObjC protocol contributes a witness table (Container1) — deliberately NOT
        // zero-witness, so it has no Box/Unbox path.
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Foundation.NSObjectProtocol") });
        Assert.False(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_ObjCPlusMarker_ReturnsFalse()
    {
        // Marker filtered, ObjC witness table remains → not zero-witness.
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("Swift.Sendable")
        });
        Assert.False(ExistentialHandler.IsZeroWitnessExistential(protocolList));
    }

    [Fact]
    public void IsZeroWitnessExistential_AgreesWithContainer0Arity()
    {
        // Cross-check: zero-witness ⟺ GetCSharpExistentialType == ExistentialContainer0.
        var marker = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        Assert.True(ExistentialHandler.IsZeroWitnessExistential(marker));
        Assert.Equal("Swift.Runtime.ExistentialContainer0", _handler.GetCSharpExistentialType(marker));

        var real = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        Assert.False(ExistentialHandler.IsZeroWitnessExistential(real));
        Assert.Equal("Swift.Runtime.ExistentialContainer1", _handler.GetCSharpExistentialType(real));
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
        var namedType = new NamedTypeSpec("ImagePipeline.DataLoading") { IsAny = true };

        var protocolList = _handler.ToProtocolListTypeSpec(namedType);
        var csType = _handler.GetCSharpExistentialType(protocolList!);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", csType);
    }

    #endregion

    #region Optional Existential Container Type

    [Fact]
    public void UnwrapOptionalExistential_SingleProtocol_ReturnsProtocolList()
    {
        // When an optional wraps an existential (e.g. Optional<any ImageDecoding>),
        // the marshal type must use ExistentialContainer1, not AnyType.
        // This test validates the handler correctly unwraps the optional existential.
        var optionalExistential = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("ImagePipeline.ImageDecoding") }));

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
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("ImagePipeline.ImageDecoding") }));

        Assert.True(_handler.IsOptionalExistential(optionalExistential));
    }

    #endregion

    #region Existential Parameter Detection

    [Fact]
    public void IsExistential_ProtocolListTypeSpec_ReturnsTrue()
    {
        // Async method filter must exclude existential parameters.
        // This test validates that IsExistential correctly identifies ProtocolListTypeSpec
        // (the existential type form used in protocol-typed method signatures).
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("VectorAnimation.VectorAnimationURLSession") });

        Assert.True(_handler.IsExistential(protocolList));
    }

    [Fact]
    public void IsExistential_NamedTypeSpecWithIsAny_ReturnsTrue()
    {
        // Existential parameter expressed as NamedTypeSpec with IsAny=true
        var existentialType = new NamedTypeSpec("VectorAnimation.VectorAnimationURLSession") { IsAny = true };

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

    #region Cross-Module Qualification Tests

    [Fact]
    public void GetPublicExistentialType_SameModule_NoNamespacePrefix()
    {
        // Protocol from same module — should NOT be namespace-qualified
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkCore" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetPublicExistentialType_DifferentModule_HasNamespacePrefix()
    {
        // Protocol from different module — SHOULD be namespace-qualified
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkFinancialConnections" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("PaymentSdkCore.IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetPublicExistentialType_NoCurrentModule_NoNamespacePrefix()
    {
        // No current module set — should NOT be namespace-qualified (backward compat)
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetPublicExistentialType_SwiftModule_NeverQualified()
    {
        // Swift stdlib protocols are never namespace-qualified
        var handler = new ExistentialHandler(_typeDatabase) { CurrentModuleName = "SomeModule" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var result = handler.GetPublicExistentialType(protocolList);

        // Swift protocols resolve to well-known types or "object" — never "Swift.IEquatable"
        Assert.DoesNotContain("Swift.", result);
    }

    [Fact]
    public void TypeProjectionFactory_CrossModuleExistential_QualifiesName()
    {
        // TypeProjectionFactory should thread CurrentModuleName to its ExistentialHandler
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var factory = new TypeProjectionFactory();
        var typeSpec = new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") { IsAny = true };

        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "PaymentSdkFinancialConnections"
        });

        Assert.NotNull(projection);
        Assert.Equal("PaymentSdkCore.IPaymentAnalyticsClientProtocol", projection!.PublicType);
    }

    [Fact]
    public void TypeProjectionFactory_SameModuleExistential_NoQualification()
    {
        // TypeProjectionFactory: same module should NOT qualify
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var factory = new TypeProjectionFactory();
        var typeSpec = new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") { IsAny = true };

        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "PaymentSdkCore"
        });

        Assert.NotNull(projection);
        Assert.Equal("IPaymentAnalyticsClientProtocol", projection!.PublicType);
    }

    [Fact]
    public void GetQualifiedProxyClassName_SameModule_NoPrefix()
    {
        // Same module — proxy name should NOT be namespace-qualified
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkCore" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetQualifiedProxyClassName(protocolList);

        Assert.Equal("PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void GetQualifiedProxyClassName_DifferentModule_HasSwiftInteropPrefix()
    {
        // Different module — proxy name SHOULD include Module.SwiftInterop prefix
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkFinancialConnections" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetQualifiedProxyClassName(protocolList);

        Assert.Equal("PaymentSdkCore.SwiftInterop.PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void GetQualifiedProxyClassName_NoCurrentModule_NoPrefix()
    {
        // No current module set — backward compat, no qualification
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.GetQualifiedProxyClassName(protocolList);

        Assert.Equal("PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void GetQualifiedProxyClassName_SwiftModule_NeverQualified()
    {
        // Swift stdlib protocols never get qualified
        var handler = new ExistentialHandler(_typeDatabase) { CurrentModuleName = "SomeModule" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var result = handler.GetQualifiedProxyClassName(protocolList);

        Assert.DoesNotContain("Swift.SwiftInterop", result);
    }

    [Fact]
    public void TypeProjectionFactory_CrossModuleExistential_QualifiesProxyName()
    {
        // The ExistentialProjection created by TypeProjectionFactory should have qualified proxy class name
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "FinancialConnectionsSDKInterface");
        var factory = new TypeProjectionFactory();
        var typeSpec = new NamedTypeSpec("PaymentSdkCore.FinancialConnectionsSDKInterface") { IsAny = true };

        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "PaymentSdkPayments"
        });

        Assert.NotNull(projection);
        // The return element conversion should reference the qualified proxy
        var conversion = projection!.GetReturnElementConversion("container");
        Assert.NotNull(conversion);
        Assert.Contains("PaymentSdkCore.SwiftInterop.FinancialConnectionsSDKInterfaceProxy", conversion);
    }

    [Fact]
    public void TypeProjectionFactory_CrossModuleOptionalExistential_QualifiesProxyName()
    {
        // Optional<any Protocol> from another module should qualify the proxy in the return plan
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "FinancialConnectionsSDKInterface");
        var factory = new TypeProjectionFactory();
        var innerTypeSpec = new NamedTypeSpec("PaymentSdkCore.FinancialConnectionsSDKInterface") { IsAny = true };
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(innerTypeSpec);

        var projection = factory.Project(optionalTypeSpec, new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "PaymentSdkPayments"
        });

        Assert.NotNull(projection);
        // PublicType should be the qualified optional interface
        Assert.Equal("PaymentSdkCore.IFinancialConnectionsSDKInterface?", projection!.PublicType);
    }

    [Fact]
    public void GetCompositionInterfaceName_ObjCFilteredToOne_CrossModuleQualified()
    {
        // P1: When ObjC filtering reduces a multi-protocol composition to 1 protocol,
        // the returned interface name should be cross-module qualified.
        // Example: `any NSObjectProtocol & AModule.FooProtocol` referenced from a different module
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkPayments" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.Equal("PaymentSdkCore.IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetCompositionInterfaceName_ObjCFilteredToOne_SameModule_NoQualification()
    {
        // Same module — no qualification even after ObjC filtering
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkCore" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetCompositionInterfaceName_ObjCFilteredToOne_NoCurrentModule_NoQualification()
    {
        // No CurrentModuleName set — backward compat, no qualification
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void QualifyProxyClassName_DifferentModule_AddsSwiftInteropPrefix()
    {
        // P2: QualifyProxyClassName should add Module.SwiftInterop prefix for cross-module proxy names
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkPayments" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.QualifyProxyClassName("PaymentAnalyticsClientProtocolProxy", protocolList);

        Assert.Equal("PaymentSdkCore.SwiftInterop.PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_SameModule_NoPrefix()
    {
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkCore" };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.QualifyProxyClassName("PaymentAnalyticsClientProtocolProxy", protocolList);

        Assert.Equal("PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_NoCurrentModule_NoPrefix()
    {
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol") });

        var result = handler.QualifyProxyClassName("PaymentAnalyticsClientProtocolProxy", protocolList);

        Assert.Equal("PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_ObjCFilteredComposition_QualifiesCorrectModule()
    {
        // QualifyProxyClassName with a mixed ObjC+Swift composition — should use the Swift module
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkPayments" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.QualifyProxyClassName("PaymentAnalyticsClientProtocolProxy", protocolList);

        Assert.Equal("PaymentSdkCore.SwiftInterop.PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_MarkerFirstThenSwiftModule_QualifiesCorrectModule()
    {
        // Regression: a composition ordered like `Swift.Sendable & OtherModule.Protocol`
        // must still qualify by OtherModule. Without the marker filter the predicate would
        // pick "Swift" first, hit the early return, and emit an unqualified proxy name —
        // defeating cross-module qualification for cross-module protocol compositions.
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "PaymentSdkPayments" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.QualifyProxyClassName("PaymentAnalyticsClientProtocolProxy", protocolList);

        Assert.Equal("PaymentSdkCore.SwiftInterop.PaymentAnalyticsClientProtocolProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_AppleUmbrellaPrintedName_CollapsesToSourceModule()
    {
        // Apple `@_implementationOnly` re-exports surface protocols whose ABI printedName
        // encodes the umbrella module ("any RealityKit.HasAnchoring") even though the
        // protocol is declared in RealityFoundation. The TypeRecord (looked up via the
        // umbrella key thanks to the TypeDatabase umbrella fallback) carries the source
        // module's namespace, which is what the rest of the emitter qualifies with. Using
        // raw `p.Module` would produce `RealityKit.SwiftInterop.HasAnchoringProxy` and dangle
        // (CS0246) — `RealityFoundation.SwiftInterop.HasAnchoringProxy` is where the proxy
        // is actually emitted.
        var db = new MockTypeDatabaseWithExplicitNamespace(
            recordKey: "RealityKit.HasAnchoring",
            csharpNamespace: "RealityFoundation",
            csharpName: "IHasAnchoring");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "RealityKit" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("RealityKit.HasAnchoring")
        });

        var result = handler.QualifyProxyClassName("HasAnchoringProxy", protocolList);

        Assert.Equal("RealityFoundation.SwiftInterop.HasAnchoringProxy", result);
    }

    [Fact]
    public void QualifyProxyClassName_AppleUmbrellaPrintedName_SameSourceModule_NoQualification()
    {
        // RealityFoundation emits its own bindings: the protocol's printedName encodes the
        // umbrella ("any RealityKit.HasAnchoring") but TypeRecord lookup resolves to
        // RealityFoundation. Once umbrella-aware resolution lands, source module equals
        // CurrentModuleName so the proxy reference stays unqualified.
        var db = new MockTypeDatabaseWithExplicitNamespace(
            recordKey: "RealityKit.HasAnchoring",
            csharpNamespace: "RealityFoundation",
            csharpName: "IHasAnchoring");
        var handler = new ExistentialHandler(db) { CurrentModuleName = "RealityFoundation" };
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("RealityKit.HasAnchoring")
        });

        var result = handler.QualifyProxyClassName("HasAnchoringProxy", protocolList);

        Assert.Equal("HasAnchoringProxy", result);
    }

    #endregion

    #region MockTypeDatabaseWithProtocol

    private class MockTypeDatabaseWithProtocol : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;
        public string AsyncLibraryName => null!;

        public MockTypeDatabaseWithProtocol(string moduleName, string protocolName)
        {
            var fqn = $"{moduleName}.{protocolName}";
            _types = new Dictionary<string, TypeRecord>
            {
                [fqn] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{protocolName}"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(fqn),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Protocol,
                    EmittedMemberCount = 0
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

    /// <summary>
    /// Mock that registers a protocol TypeRecord whose lookup key may differ from its
    /// CSharpTypeName.Namespace. Mirrors Apple's umbrella collapse: the lookup is keyed
    /// on the umbrella module ("RealityKit.HasAnchoring") but the C# emission lives in
    /// the source module's namespace ("RealityFoundation").
    /// </summary>
    private class MockTypeDatabaseWithExplicitNamespace : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;
        public string AsyncLibraryName => null!;

        public MockTypeDatabaseWithExplicitNamespace(string recordKey, string csharpNamespace, string csharpName)
        {
            _types = new Dictionary<string, TypeRecord>
            {
                [recordKey] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csharpNamespace, csharpName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(recordKey),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Protocol,
                    EmittedMemberCount = 0
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);

        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region Marker Protocol Tests

    [Theory]
    [InlineData("Swift.Sendable", true)]
    [InlineData("Swift.Escapable", true)]
    [InlineData("Swift.Copyable", true)]
    [InlineData("Swift.SendableMetatype", true)]
    [InlineData("SomeModule.Sendable", true)]
    [InlineData("Swift.Equatable", false)]
    [InlineData("Swift.Codable", false)]
    [InlineData("ImagePipeline.DataLoading", false)]
    public void IsMarkerProtocol_IdentifiesMarkers(string protocolName, bool expected)
    {
        var protocol = new NamedTypeSpec(protocolName);
        Assert.Equal(expected, ExistentialHandler.IsMarkerProtocol(protocol));
    }

    [Fact]
    public void GetEffectiveProtocols_FiltersMarkers()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("Swift.Codable")
        });

        var effective = ExistentialHandler.GetEffectiveProtocols(protocolList);

        Assert.Single(effective);
        Assert.Equal("Swift.Codable", effective[0].Name);
    }

    [Fact]
    public void GetEffectiveProtocols_PureMarker_ReturnsEmpty()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable")
        });

        var effective = ExistentialHandler.GetEffectiveProtocols(protocolList);

        Assert.Empty(effective);
    }

    [Fact]
    public void GetEffectiveProtocols_FiltersObjCAndMarkers()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var effective = ExistentialHandler.GetEffectiveProtocols(protocolList);

        Assert.Single(effective);
        Assert.Equal("PaymentSdkCore.PaymentAnalyticsClientProtocol", effective[0].Name);
    }

    [Fact]
    public void GetCSharpExistentialType_AnySendable_ReturnsEC0()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable")
        });

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer0", result);
    }

    [Fact]
    public void GetCSharpExistentialType_SendableAndCodable_ReturnsEC1()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("Swift.Codable")
        });

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    [Fact]
    public void GetExistentialContainerSizeInWords_AnySendable_Returns4()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable")
        });

        // 3 payload + 1 metadata + 0 witness tables = 4 words
        Assert.Equal(4, _handler.GetExistentialContainerSizeInWords(protocolList));
    }

    [Fact]
    public void GetPublicExistentialType_AnySendable_ReturnsObject()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable")
        });

        var result = _handler.GetPublicExistentialType(protocolList);

        Assert.Equal("object", result);
    }

    [Fact]
    public void GetPublicExistentialType_SendableAndKnownProtocol_ReturnsSingleProtocolInterface()
    {
        // any Sendable & PaymentAnalyticsClientProtocol → IPaymentAnalyticsClientProtocol
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void AllProtocolsHaveTypeRecords_PureMarker_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable")
        });

        Assert.True(_handler.AllProtocolsHaveTypeRecords(protocolList));
    }

    [Fact]
    public void AllProtocolsHaveTypeRecords_MarkerAndKnownProtocol_ReturnsTrue()
    {
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        Assert.True(handler.AllProtocolsHaveTypeRecords(protocolList));
    }

    [Fact]
    public void EffectiveProtocolsHaveTypeRecords_ObjCBridgedAndKnownProtocol_ReturnsTrue()
    {
        // ObjC-bridged participants are filtered out of the emitted composition
        // (GetCompositionInterfaceName uses GetEffectiveProtocols), so the gate
        // used by GetPublicExistentialType must mirror that. Without this, a
        // bare `NSCoding & PaymentAnalyticsClientProtocol` composition would
        // collapse to `object` instead of emitting as `IPaymentAnalyticsClientProtocol`.
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSCoding"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        Assert.True(handler.EffectiveProtocolsHaveTypeRecords(protocolList));
        Assert.Equal("IPaymentAnalyticsClientProtocol", handler.GetPublicExistentialType(protocolList));
    }

    [Fact]
    public void TryGetFilteredProxyClassName_SendableAndCodable_ReturnsCodableProxy()
    {
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = _handler.TryGetFilteredProxyClassName(protocolList, out var proxyClassName);

        Assert.True(result);
        Assert.Equal("PaymentAnalyticsClientProtocolProxy", proxyClassName);
    }

    [Fact]
    public void GetCompositionInterfaceName_MarkersFiltered_ReturnsSingleInterface()
    {
        var db = new MockTypeDatabaseWithProtocol("PaymentSdkCore", "PaymentAnalyticsClientProtocol");
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.Equal("IPaymentAnalyticsClientProtocol", result);
    }

    [Fact]
    public void GetNonMarkerProtocols_KeepsObjC_FiltersMarkers()
    {
        // ObjC protocols have witness tables — must be kept for ABI
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("Swift.Sendable"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var nonMarker = ExistentialHandler.GetNonMarkerProtocols(protocolList);

        Assert.Equal(2, nonMarker.Count); // NSObjectProtocol + PaymentAnalyticsClientProtocol
        Assert.Contains(nonMarker, p => p.Name == "Foundation.NSObjectProtocol");
        Assert.Contains(nonMarker, p => p.Name == "PaymentSdkCore.PaymentAnalyticsClientProtocol");
    }

    [Fact]
    public void GetCSharpExistentialType_ObjCAndMarker_IncludesObjCInCount()
    {
        // any NSObjectProtocol & Sendable → EC1 (NSObjectProtocol has a witness table)
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("Swift.Sendable")
        });

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    [Fact]
    public void GetCSharpExistentialType_ObjCAndSwift_IncludesBothInCount()
    {
        // any NSObjectProtocol & MyProtocol → EC2 (both have witness tables)
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("PaymentSdkCore.PaymentAnalyticsClientProtocol")
        });

        var result = _handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer2", result);
    }

    [Fact]
    public void GetExistentialContainerSizeInWords_ObjCProtocol_IncludesWitnessTable()
    {
        // any NSObjectProtocol → 4 + 1 = 5 words (ObjC has witness table)
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol")
        });

        Assert.Equal(5, _handler.GetExistentialContainerSizeInWords(protocolList));
    }

    [Fact]
    public void AllProtocolsHaveTypeRecords_ObjCOnly_ReturnsFalse()
    {
        // ObjC-only existentials don't have TypeRecords — must NOT vacuously succeed
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol")
        });

        Assert.False(_handler.AllProtocolsHaveTypeRecords(protocolList));
    }

    [Fact]
    public void AllProtocolsHaveTypeRecords_ObjCAndMarker_ReturnsFalse()
    {
        // ObjC + marker: marker is skipped, but ObjC still checked → false
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObjectProtocol"),
            new NamedTypeSpec("Swift.Sendable")
        });

        Assert.False(_handler.AllProtocolsHaveTypeRecords(protocolList));
    }

    #endregion

    #region CompositionHasNonProtocolParticipant Tests

    [Fact]
    public void CompositionHasNonProtocolParticipant_PureProtocolComposition_ReturnsFalse()
    {
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.P1", TypeRecordKind.Protocol);
        db.Register("TestModule.P2", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.P1"),
            new NamedTypeSpec("TestModule.P2"),
        });

        Assert.False(handler.CompositionHasNonProtocolParticipant(protocolList));
    }

    [Fact]
    public void CompositionHasNonProtocolParticipant_SwiftClassParticipant_ReturnsTrue()
    {
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.AnyClass", TypeRecordKind.Class);
        db.Register("TestModule.SomeProtocol", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.AnyClass"),
            new NamedTypeSpec("TestModule.SomeProtocol"),
        });

        Assert.True(handler.CompositionHasNonProtocolParticipant(protocolList));
    }

    [Fact]
    public void CompositionHasNonProtocolParticipant_NSObjectParticipant_ReturnsTrue()
    {
        // Foundation.NSObject is an ObjC-module type — previously filtered by
        // GetEffectiveProtocols before reaching the class-bounded check. Must still
        // fire for class-bounded existentials like `any NSObject & MyProtocol`.
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.MyProtocol", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObject"),
            new NamedTypeSpec("TestModule.MyProtocol"),
        });

        Assert.True(handler.CompositionHasNonProtocolParticipant(protocolList));
    }

    [Fact]
    public void CompositionHasNonProtocolParticipant_ObjCProtocolParticipant_ReturnsFalse()
    {
        // NSCoding is an ObjC protocol, not a class. A composition with only protocol
        // participants (ObjC or otherwise) must not be flagged as class-bounded.
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.MyProtocol", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSCoding"),
            new NamedTypeSpec("TestModule.MyProtocol"),
        });

        Assert.False(handler.CompositionHasNonProtocolParticipant(protocolList));
    }

    [Fact]
    public void CompositionHasNonProtocolParticipant_MarkerProtocolOnly_ReturnsFalse()
    {
        var db = new ClassBoundCompositionMockDatabase();
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Sendable"),
        });

        Assert.False(handler.CompositionHasNonProtocolParticipant(protocolList));
    }

    [Fact]
    public void GetPublicExistentialType_ClassBoundComposition_CollapsesToObject()
    {
        // NSObject & MyProtocol must degrade to `object` — the class-bounded layout
        // cannot be represented by a synthesised composition interface.
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.MyProtocol", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSObject"),
            new NamedTypeSpec("TestModule.MyProtocol"),
        });

        Assert.Equal("object", handler.GetPublicExistentialType(protocolList));
    }

    #endregion

    #region IsClassBoundArity1Existential Tests

    // This predicate is the single decision point that routes every class-bound existential
    // heap-cell READ (array element, scalar/optional method return, property getter, async
    // return, @convention(c) closure parameter, enum payload) to the compact 2-word
    // ClassExistentialContainer1 read instead of the 5-word opaque ExistentialContainer1.
    // A wrong answer here over-reads 24 bytes past a 16-byte allocation (SIGSEGV / heap
    // corruption), so it is exercised directly here in addition to the end-to-end BindingTests.

    [Fact]
    public void IsClassBoundArity1Existential_SingleClassBoundProtocol_ReturnsTrue()
    {
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.BoundProto", TypeRecordKind.Protocol,
            TypeRecordFlags.Frozen | TypeRecordFlags.ClassBound);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.BoundProto") });

        Assert.True(handler.IsClassBoundArity1Existential(protocolList));
    }

    [Fact]
    public void IsClassBoundArity1Existential_SingleOpaqueProtocol_ReturnsFalse()
    {
        // Same arity-1 shape but WITHOUT the ClassBound flag: the opaque 5-word path must win.
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.OpaqueProto", TypeRecordKind.Protocol);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OpaqueProto") });

        Assert.False(handler.IsClassBoundArity1Existential(protocolList));
    }

    [Fact]
    public void IsClassBoundArity1Existential_ClassBoundPlusMarker_ReturnsTrue()
    {
        // A trailing marker (Sendable) is filtered by GetNonMarkerProtocols, so the
        // composition is still arity 1 and class-bound — the read width must stay compact.
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.BoundProto", TypeRecordKind.Protocol,
            TypeRecordFlags.Frozen | TypeRecordFlags.ClassBound);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.BoundProto"),
            new NamedTypeSpec("Swift.Sendable"),
        });

        Assert.True(handler.IsClassBoundArity1Existential(protocolList));
    }

    [Fact]
    public void IsClassBoundArity1Existential_MultipleNonMarkerProtocols_ReturnsFalse()
    {
        // Arity > 1 is never the single class-bound layout (multi-protocol class-bound
        // compositions are rejected upstream and collapse to object).
        var db = new ClassBoundCompositionMockDatabase();
        db.Register("TestModule.BoundProto", TypeRecordKind.Protocol,
            TypeRecordFlags.Frozen | TypeRecordFlags.ClassBound);
        db.Register("TestModule.OtherProto", TypeRecordKind.Protocol,
            TypeRecordFlags.Frozen | TypeRecordFlags.ClassBound);
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.BoundProto"),
            new NamedTypeSpec("TestModule.OtherProto"),
        });

        Assert.False(handler.IsClassBoundArity1Existential(protocolList));
    }

    [Fact]
    public void IsClassBoundArity1Existential_UnknownProtocol_ReturnsFalse()
    {
        // No TypeRecord → cannot prove class-boundedness → fail closed to the opaque path.
        var db = new ClassBoundCompositionMockDatabase();
        var handler = new ExistentialHandler(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Unregistered") });

        Assert.False(handler.IsClassBoundArity1Existential(protocolList));
    }

    [Fact]
    public void IsClassBoundArity1Existential_EmptyProtocolList_ReturnsFalse()
    {
        var handler = new ExistentialHandler(new ClassBoundCompositionMockDatabase());

        Assert.False(handler.IsClassBoundArity1Existential(new ProtocolListTypeSpec()));
    }

    #endregion

    #region ClassBoundCompositionMockDatabase

    /// <summary>
    /// Minimal TypeDatabase that lets tests register arbitrary TypeRecords with a
    /// specific <see cref="TypeRecordKind"/> and flags, so <c>CompositionHasNonProtocolParticipant</c>
    /// and <c>IsClassBoundArity1Existential</c> can be exercised against the boundaries they guard.
    /// </summary>
    private class ClassBoundCompositionMockDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();
        public string AsyncLibraryName => null!;

        public void Register(string moduleQualifiedName, TypeRecordKind kind, TypeRecordFlags flags = TypeRecordFlags.Frozen)
        {
            _types[moduleQualifiedName] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("N", "T"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
                MetadataAccessor = "",
                Flags = flags,
                Kind = kind,
                EmittedMemberCount = 0,
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);

        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region Existential Union Tests

    [Fact]
    public void GetPublicExistentialType_PATProtocolWithKnownConformers_ReturnsExistentialUnion()
    {
        // Protocol with HasAssociatedTypes — normally returns "object".
        // With SpecializationEngine providing conformers AND a pure-read (return) position
        // (allowUnionProjection: true), should return ExistentialUnion.
        var db = new PATProtocolMockDatabase("SwiftBindingsTestLib.AttributeKind");
        var engine = new ConcreteSpecializationEngine(new EmptyTypeDatabase());

        var handler = new ExistentialHandler(db) { SpecializationEngine = engine };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("SwiftBindingsTestLib.AttributeKind") });

        var result = handler.GetPublicExistentialType(protocolList, allowUnionProjection: true);

        Assert.Equal("Swift.Runtime.ExistentialUnion", result);
    }

    [Fact]
    public void GetPublicExistentialType_PATProtocolWithConformers_DefaultDirection_ReturnsObject()
    {
        // Direction safety: even with a configured engine AND known conformers, an input/setter
        // position (allowUnionProjection defaults to false) MUST keep degrading to "object" —
        // ExistentialUnion is a read-only Swift→C# wrapper with no input marshalling. This is the
        // guard that lets the engine be wired onto the env handler unconditionally without flipping
        // parameters/setters to an unmarshallable type.
        var db = new PATProtocolMockDatabase("SwiftBindingsTestLib.AttributeKind");
        var engine = new ConcreteSpecializationEngine(new EmptyTypeDatabase());

        var handler = new ExistentialHandler(db) { SpecializationEngine = engine };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("SwiftBindingsTestLib.AttributeKind") });

        var result = handler.GetPublicExistentialType(protocolList); // default allowUnionProjection: false

        Assert.Equal("object", result);
    }

    [Fact]
    public void GetPublicExistentialType_PATProtocolWithoutConformers_ReturnsObject()
    {
        // PAT protocol with no known conformers — should fall back to "object" even in a read position.
        var db = new PATProtocolMockDatabase("TestLib.UnknownProtocol");
        var engine = new ConcreteSpecializationEngine(new EmptyTypeDatabase());

        var handler = new ExistentialHandler(db) { SpecializationEngine = engine };
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestLib.UnknownProtocol") });

        var result = handler.GetPublicExistentialType(protocolList, allowUnionProjection: true);

        Assert.Equal("object", result);
    }

    [Fact]
    public void GetPublicExistentialType_PATProtocolWithoutEngine_ReturnsObject()
    {
        // PAT protocol without specialization engine — should fall back to "object" even in a read position.
        var db = new PATProtocolMockDatabase("SwiftBindingsTestLib.AttributeKind");

        var handler = new ExistentialHandler(db); // no SpecializationEngine
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("SwiftBindingsTestLib.AttributeKind") });

        var result = handler.GetPublicExistentialType(protocolList, allowUnionProjection: true);

        Assert.Equal("object", result);
    }

    /// <summary>
    /// Mock database that registers a single protocol with HasAssociatedTypes flag.
    /// </summary>
    private class PATProtocolMockDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();
        public string AsyncLibraryName => null!;

        public PATProtocolMockDatabase(string protocolName)
        {
            _types[protocolName] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestLib", protocolName.Split('.').Last()),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    /// <summary>Minimal ITypeDatabase that always returns false.</summary>
    private class EmptyTypeDatabase : ITypeDatabase
    {
        public string AsyncLibraryName => null!;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) { record = null!; return false; }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
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
                    CSharpTypeName = CSharpTypeName.NIntType,
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
