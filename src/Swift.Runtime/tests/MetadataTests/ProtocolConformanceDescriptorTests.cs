// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ProtocolConformanceDescriptorTests : IClassFixture<ProtocolConformanceDescriptorTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public ProtocolConformanceDescriptorTests(TestFixture fixture)
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

    [Fact]
    public static void RetrievesExistingProtocolConformanceDescriptor()
    {
        var descriptor = ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", "$sSiSHsMc");
        Assert.True(descriptor.IsValid);
    }

    [Fact]
    public static void FailsToRetrieveNonExistentProtocolConformanceDescriptor()
    {
        Assert.Throws<SwiftRuntimeException>(() => ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", "nonExistentSymbol"));
    }

    [Fact]
    public static void FailsToRetrieveFromNonExistentLibrary()
    {
        Assert.Throws<SwiftRuntimeException>(() => ProtocolConformanceDescriptor.LoadFromSymbol("nonExistentLibrary", "nonExistentSymbol"));
    }

    [Fact]
    public static void LoadFromSymbol_FallsBackToFrameworkPath()
    {
        // On macOS, swiftCore is available as both a bare dylib path and as a framework.
        // This tests that LoadFromSymbol can find symbols when the bare name resolves
        // (the @rpath fallback is only needed on iOS device where the DllImport resolver
        // is on the binding assembly, not Swift.Runtime — but we verify the fallback
        // doesn't break working paths and that non-existent names still fail correctly).
        var descriptor = ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", "$sSiSHsMc");
        Assert.True(descriptor.IsValid);

        // Non-existent library should still throw even with the @rpath fallback
        Assert.Throws<SwiftRuntimeException>(() =>
            ProtocolConformanceDescriptor.LoadFromSymbol("CompletelyFakeLibrary", "$sSiSHsMc"));
    }

    [Fact]
    public static void RetrievesProtocolConformanceDescriptorUsingStaticAccessor()
    {
        var result = ProtocolConformanceDescriptor.TryGet<SwiftIntMock, ISwiftHashable>(out var descriptor);
        Assert.True(result);
        Assert.NotNull(descriptor);
        Assert.True(descriptor!.Value.IsValid);
    }

    [Fact]
    public static void FailsToRetrieveProtocolConformanceDescriptorUsingStaticAccessorWhenTypeDoesNotConformToProtocol()
    {
        var result = ProtocolConformanceDescriptor.TryGet<TypeNotImplementingAnyProtocols, ISwiftHashable>(out var _);
        Assert.False(result);
    }

    [Fact]
    public static void TryGetDirect_RetrievesDescriptorWithoutReflection()
    {
        // TryGetDirect uses the ISwiftObject constraint to call the static abstract
        // method directly, avoiding MakeGenericType (NativeAOT-safe path).
        var result = ProtocolConformanceDescriptor.TryGetDirect<SwiftIntMock, ISwiftHashable>(out var descriptor);
        Assert.True(result);
        Assert.NotNull(descriptor);
        Assert.True(descriptor!.Value.IsValid);
    }

    [Fact]
    public static void TryGetDirect_ReturnsFalseForInvalidDescriptor()
    {
        // AnyTypeMock returns ProtocolConformanceDescriptor.Zero (invalid)
        var result = ProtocolConformanceDescriptor.TryGetDirect<AnyTypeMock, ISwiftHashable>(out var descriptor);
        Assert.False(result);
        Assert.Null(descriptor);
    }

    [Fact]
    public static void TryGetDirect_MatchesTryGetResult()
    {
        // Both paths should return the same descriptor for the same type/protocol pair.
        ProtocolConformanceDescriptor.TryGet<SwiftIntMock, ISwiftHashable>(out var reflectionResult);
        ProtocolConformanceDescriptor.TryGetDirect<SwiftIntMock, ISwiftHashable>(out var directResult);

        Assert.Equal(reflectionResult, directResult);
    }
}
