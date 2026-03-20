// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

public class ProtocolWitnessTableTests : IClassFixture<ProtocolWitnessTableTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public ProtocolWitnessTableTests(TestFixture fixture)
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
    public static void TryGetRetrievesProtocolWitnessTable()
    {
        var result = ProtocolWitnessTable.TryGet<SwiftIntMock, ISwiftHashable>(out var protocolWitnessTable);
        Assert.True(result);
        Assert.NotNull(protocolWitnessTable);
        Assert.True(protocolWitnessTable!.Value.IsValid);
    }

    [Fact]
    public static void TryGetFailsToRetrieveProtocolWitnessTableWhenTypeDoesNotConformToProtocol()
    {
        var result = ProtocolWitnessTable.TryGet<TypeNotImplementingAnyProtocols, ISwiftHashable>(out var protocolWitnessTable);
        Assert.False(result);
        Assert.Null(protocolWitnessTable);
    }

    [Fact]
    public static void GetOrThrowRetrievesProtocolWitnessTable()
    {
        var protocolWitnessTable = ProtocolWitnessTable.GetOrThrow<SwiftIntMock, ISwiftHashable>();
        Assert.True(protocolWitnessTable.IsValid);
    }

    [Fact]
    public static void GetOrThrowThrowsWhenTypeDoesNotConformToProtocol()
    {
        Assert.Throws<SwiftRuntimeException>(() => ProtocolWitnessTable.GetOrThrow<TypeNotImplementingAnyProtocols, ISwiftHashable>());
    }

    [Fact]
    public static void FailsToRetrieveProtocolWitnessTableWhenProtocolConformanceDescriptorInvalid()
    {
        // .NET 10 may throw SwiftRuntimeException directly instead of wrapping in TargetInvocationException
        var exception = Record.Exception(() => ProtocolWitnessTable.GetOrThrow<AnyTypeMock, ISwiftHashable>());
        Assert.NotNull(exception);
        if (exception is System.Reflection.TargetInvocationException tie)
            Assert.IsType<InvalidOperationException>(tie.InnerException);
        else
            Assert.IsType<SwiftRuntimeException>(exception);
    }

    [Fact]
    public static void GetOrThrowDirect_RetrievesWitnessTableWithoutReflection()
    {
        // GetOrThrowDirect uses ISwiftObject constraint to avoid MakeGenericType (NativeAOT-safe).
        var witnessTable = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();
        Assert.True(witnessTable.IsValid);
    }

    [Fact]
    public static void GetOrThrowDirect_MatchesGetOrThrowResult()
    {
        // Both paths should return the same witness table handle.
        var reflectionResult = ProtocolWitnessTable.GetOrThrow<SwiftIntMock, ISwiftHashable>();
        var directResult = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();

        Assert.Equal(reflectionResult.Handle, directResult.Handle);
    }

    [Fact]
    public static void GetOrThrowDirect_ThrowsForInvalidConformance()
    {
        // AnyTypeMock returns invalid metadata and conformance descriptors.
        // GetOrThrowDirect should throw (metadata check fails first).
        Assert.ThrowsAny<Exception>(() =>
            ProtocolWitnessTable.GetOrThrowDirect<AnyTypeMock, ISwiftHashable>());
    }

    [Fact]
    public static void RegisterWitnessTable_PreRegistersForGetOrThrow()
    {
        // Pre-register a witness table via SwiftMarshal.RegisterWitnessTable (the same
        // call the generated [ModuleInitializer] emits). Then verify GetOrThrow returns
        // the cached value, matching the direct dispatch result.
        SwiftMarshal.RegisterWitnessTable<SwiftIntMock, ISwiftHashable>();

        var cached = ProtocolWitnessTable.GetOrThrow<SwiftIntMock, ISwiftHashable>();
        var direct = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();

        Assert.True(cached.IsValid);
        Assert.Equal(direct.Handle, cached.Handle);
    }

    [Fact]
    public static void WitnessTableDispatcher_TryGet_ReturnsFalseForUnregisteredType()
    {
        // TypeNotImplementingAnyProtocols was never registered — TryGet should return false.
        var found = WitnessTableDispatcher.TryGet(
            typeof(TypeNotImplementingAnyProtocols), typeof(ISwiftHashable), out var witnessTable);

        Assert.False(found);
        Assert.False(witnessTable.IsValid);
    }

    [Fact]
    public static void WitnessTableDispatcher_Register_IsIdempotent()
    {
        // Registering the same (type, protocol) pair twice should not throw.
        var wt = ProtocolWitnessTable.GetOrThrowDirect<SwiftIntMock, ISwiftHashable>();
        WitnessTableDispatcher.Register(typeof(SwiftIntMock), typeof(ISwiftHashable), wt);
        WitnessTableDispatcher.Register(typeof(SwiftIntMock), typeof(ISwiftHashable), wt);

        var found = WitnessTableDispatcher.TryGet(
            typeof(SwiftIntMock), typeof(ISwiftHashable), out var cached);

        Assert.True(found);
        Assert.Equal(wt.Handle, cached.Handle);
    }
}
