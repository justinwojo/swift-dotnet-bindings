// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 32 (perf half): on Mono/CoreCLR the marshalling and dispatch helpers must
/// consult the factory caches (populated by generated module initializers on all runtimes)
/// BEFORE falling back to per-call reflection. These tests run on the desktop CoreCLR host,
/// where <c>SwiftRuntimeInfo.IsNativeAotRuntime</c> is false — i.e. exactly the non-AOT branch
/// the fix de-reflects. Each fake type's static-abstract members throw, so a cache hit is
/// proven by the absence of that throw (a reflection fallback would invoke the throwing
/// static member).
/// </summary>
public class CacheFirstDispatchTests
{
    // Distinct fake types per scenario so the process-wide dispatcher caches don't collide.

    private sealed class CacheHitMarshalFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public CacheHitMarshalFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload)
            => throw new InvalidOperationException("static-abstract NewFromPayload must not run when a factory is cached");
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class CacheHitHelperFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public CacheHitHelperFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload)
            => throw new InvalidOperationException("static-abstract NewFromPayload must not run when a factory is cached");
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class CacheMissReflectionFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public CacheMissReflectionFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        // No factory is registered for this type, so the reflection fallback must find and
        // invoke this static member and round-trip the handle.
        public static ISwiftObject NewFromPayload(IntPtr payload) => new CacheMissReflectionFake(payload);
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class ConformanceCacheFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public ConformanceCacheFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new InvalidOperationException("static-abstract GetProtocolConformanceDescriptor must not run when a factory is cached");
    }

    private interface IConformanceFakeProtocol { }

    [Fact]
    public void MarshalFromSwiftObject_CacheHit_UsesFactoryNotReflection()
    {
        var handle = new IntPtr(0x1234);
        NewFromPayloadDispatcher.Register(typeof(CacheHitMarshalFake),
            h => new CacheHitMarshalFake(h));

        var result = SwiftMarshal.MarshalFromSwiftObject<CacheHitMarshalFake>(handle);

        Assert.NotNull(result);
        Assert.Equal(handle, result.Handle);
    }

    [Fact]
    public void SwiftObjectHelper_NewFromPayload_CacheHit_UsesFactoryNotReflection()
    {
        var handle = new IntPtr(0x5678);
        NewFromPayloadDispatcher.Register(typeof(CacheHitHelperFake),
            h => new CacheHitHelperFake(h));

        var result = SwiftObjectHelper<CacheHitHelperFake>.NewFromPayload(handle);

        var typed = Assert.IsType<CacheHitHelperFake>(result);
        Assert.Equal(handle, typed.Handle);
    }

    [Fact]
    public void MarshalFromSwiftObject_CacheMiss_FallsBackToReflection()
    {
        // No factory registered → reflection fallback must still create the instance.
        var handle = new IntPtr(0x9ABC);
        var result = SwiftMarshal.MarshalFromSwiftObject<CacheMissReflectionFake>(handle);

        Assert.NotNull(result);
        Assert.Equal(handle, result.Handle);
    }

    [Fact]
    public void ProtocolConformanceDescriptor_TryGet_CacheFirst_DoesNotReflect()
    {
        // A registered factory is authoritative on all runtimes. Returning Zero means "no
        // conformance" without ever reaching the throwing static-abstract via reflection.
        ConformanceDispatcher.Register(typeof(ConformanceCacheFake), typeof(IConformanceFakeProtocol),
            () => ProtocolConformanceDescriptor.Zero);

        var found = ProtocolConformanceDescriptor.TryGet<ConformanceCacheFake, IConformanceFakeProtocol>(out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void RuntimeContract_AssertCompatible_MatchingVersion_DoesNotThrow()
    {
        // The runtime's own version is always compatible with itself.
        RuntimeContract.AssertCompatible(RuntimeContract.Version);
    }

    [Fact]
    public void RuntimeContract_AssertCompatible_MismatchedVersion_Throws()
    {
        var ex = Assert.Throws<SwiftRuntimeContractMismatchException>(
            () => RuntimeContract.AssertCompatible(RuntimeContract.Version + 1));
        Assert.Equal(RuntimeContract.Version + 1, ex.GeneratedAgainstVersion);
        Assert.Equal(RuntimeContract.Version, ex.RuntimeVersion);
    }
}
