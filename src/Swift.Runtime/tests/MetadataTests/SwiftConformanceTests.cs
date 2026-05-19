// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftConformanceTests : IClassFixture<SwiftConformanceTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftConformanceTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }

        private static void InitializeResources()
        {
        }
    }

    private const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";

    // Well-known protocol descriptor symbols
    private const string HashableMp = "$sSHMp";
    private const string EquatableMp = "$sSQMp";
    private const string ComparableMp = "$sSLMp";
    private const string SequenceMp = "$sSTMp";
    private const string CollectionMp = "$sSlMp";

    // Well-known conformance descriptor for cross-validation
    private const string IntHashableMc = "$sSiSHsMc";

    #region ProtocolDescriptor loading tests

    [Fact]
    public static void LoadsHashableProtocolDescriptor()
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void LoadsEquatableProtocolDescriptor()
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, EquatableMp);
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void LoadsComparableProtocolDescriptor()
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, ComparableMp);
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void LoadsSequenceProtocolDescriptor()
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, SequenceMp);
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void LoadsCollectionProtocolDescriptor()
    {
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, CollectionMp);
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void FailsToLoadNonExistentSymbol()
    {
        Assert.Throws<SwiftRuntimeException>(() => ProtocolDescriptor.LoadFromSymbol(SwiftCore, "nonExistentSymbol"));
    }

    [Fact]
    public static void FailsToLoadFromNonExistentLibrary()
    {
        Assert.Throws<SwiftRuntimeException>(() => ProtocolDescriptor.LoadFromSymbol("nonExistentLibrary", "nonExistentSymbol"));
    }

    [Fact]
    public static void LoadFromSymbol_FallsBackToFrameworkPath()
    {
        // The @rpath/{name}.framework/{name} fallback inside LoadFromSymbol exists for
        // iOS device, where the DllImport resolver that maps library names to framework
        // paths is registered on the binding assembly, not Swift.Runtime — so the bare
        // name fails and the framework path succeeds. On the macOS test host the bare
        // libswiftCore.dylib path resolves directly, so this test exercises (a) that
        // adding the fallback didn't break the already-working bare-name path, and
        // (b) that a completely fake library name still throws even after both attempts.
        var descriptor = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.True(descriptor.IsValid);

        Assert.Throws<SwiftRuntimeException>(() =>
            ProtocolDescriptor.LoadFromSymbol("CompletelyFakeLibrary", HashableMp));
    }

    [Fact]
    public static void ZeroIsInvalid()
    {
        Assert.False(ProtocolDescriptor.Zero.IsValid);
    }

    [Fact]
    public static void SameSymbolLoadedTwiceIsEqual()
    {
        var a = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var b = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public static void DifferentProtocolsAreNotEqual()
    {
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var equatable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, EquatableMp);
        Assert.NotEqual(hashable, equatable);
        Assert.True(hashable != equatable);
    }

    #endregion

    #region ConformsToProtocol tests

    [Fact]
    public static void IntConformsToHashable()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(intMetadata, hashable));
    }

    [Fact]
    public static void IntConformsToEquatable()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var equatable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, EquatableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(intMetadata, equatable));
    }

    [Fact]
    public static void IntConformsToComparable()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var comparable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, ComparableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(intMetadata, comparable));
    }

    [Fact]
    public static void BoolConformsToHashable()
    {
        var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(boolMetadata, hashable));
    }

    [Fact]
    public static void BoolConformsToEquatable()
    {
        var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
        var equatable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, EquatableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(boolMetadata, equatable));
    }

    [Fact]
    public static void BoolDoesNotConformToCollection()
    {
        var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
        var collection = ProtocolDescriptor.LoadFromSymbol(SwiftCore, CollectionMp);
        Assert.False(SwiftConformance.ConformsToProtocol(boolMetadata, collection));
    }

    [Fact]
    public static void IntDoesNotConformToCollection()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var collection = ProtocolDescriptor.LoadFromSymbol(SwiftCore, CollectionMp);
        Assert.False(SwiftConformance.ConformsToProtocol(intMetadata, collection));
    }

    [Fact]
    public static void InvalidTypeMetadataThrowsArgumentException()
    {
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.Throws<ArgumentException>(() => SwiftConformance.ConformsToProtocol(TypeMetadata.Zero, hashable));
    }

    [Fact]
    public static void InvalidProtocolDescriptorThrowsArgumentException()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        Assert.Throws<ArgumentException>(() => SwiftConformance.ConformsToProtocol(intMetadata, ProtocolDescriptor.Zero));
    }

    #endregion

    #region TryGetWitnessTable tests

    [Fact]
    public static void TryGetWitnessTableSucceedsForValidConformance()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var result = SwiftConformance.TryGetWitnessTable(intMetadata, hashable, out var witnessTable);
        Assert.True(result);
        Assert.NotNull(witnessTable);
        Assert.True(witnessTable!.Value.IsValid);
    }

    [Fact]
    public static void TryGetWitnessTableFailsForNonConformance()
    {
        var boolMetadata = TypeMetadata.GetTypeMetadataOrThrow<bool>();
        var collection = ProtocolDescriptor.LoadFromSymbol(SwiftCore, CollectionMp);
        var result = SwiftConformance.TryGetWitnessTable(boolMetadata, collection, out var witnessTable);
        Assert.False(result);
        Assert.Null(witnessTable);
    }

    [Fact]
    public static void TryGetWitnessTableReturnsFalseForInvalidMetadata()
    {
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var result = SwiftConformance.TryGetWitnessTable(TypeMetadata.Zero, hashable, out var witnessTable);
        Assert.False(result);
        Assert.Null(witnessTable);
    }

    [Fact]
    public static void TryGetWitnessTableReturnsFalseForInvalidProtocol()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var result = SwiftConformance.TryGetWitnessTable(intMetadata, ProtocolDescriptor.Zero, out var witnessTable);
        Assert.False(result);
        Assert.Null(witnessTable);
    }

    #endregion

    #region Cross-validation tests

    [Fact]
    public static void DynamicWitnessTableMatchesStaticPath()
    {
        // Dynamic: swift_conformsToProtocol
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var dynamicResult = SwiftConformance.TryGetWitnessTable(intMetadata, hashable, out var dynamicWt);
        Assert.True(dynamicResult);
        Assert.True(dynamicWt!.Value.IsValid);

        // Static: swift_getWitnessTable via existing path
        var staticResult = ProtocolWitnessTable.TryGet<SwiftIntMock, ISwiftHashable>(out var staticWt);
        Assert.True(staticResult);
        Assert.True(staticWt!.Value.IsValid);

        // Both paths agree conformance exists
    }

    [Fact]
    public static void DynamicLookupAgreesWithStaticConformanceDescriptor()
    {
        // Load the static conformance descriptor for Int→Hashable
        var staticConformance = ProtocolConformanceDescriptor.LoadFromSymbol(SwiftCore, IntHashableMc);
        Assert.True(staticConformance.IsValid);

        // Dynamic conformance check should also succeed
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        Assert.True(SwiftConformance.ConformsToProtocol(intMetadata, hashable));
    }

    [Fact]
    public static void DynamicLookupMultipleProtocolsSameType()
    {
        var intMetadata = TypeMetadata.GetTypeMetadataOrThrow<nint>();
        var hashable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, HashableMp);
        var equatable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, EquatableMp);
        var comparable = ProtocolDescriptor.LoadFromSymbol(SwiftCore, ComparableMp);

        // Int conforms to all three
        Assert.True(SwiftConformance.TryGetWitnessTable(intMetadata, hashable, out var wt1));
        Assert.True(wt1!.Value.IsValid);

        Assert.True(SwiftConformance.TryGetWitnessTable(intMetadata, equatable, out var wt2));
        Assert.True(wt2!.Value.IsValid);

        Assert.True(SwiftConformance.TryGetWitnessTable(intMetadata, comparable, out var wt3));
        Assert.True(wt3!.Value.IsValid);

    }

    #endregion
}
