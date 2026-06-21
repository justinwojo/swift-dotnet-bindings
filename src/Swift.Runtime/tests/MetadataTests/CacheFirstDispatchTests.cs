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
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
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
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
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
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        // No factory is registered for this type, so the reflection fallback must find and
        // invoke this static member and round-trip the handle.
        public static ISwiftObject NewFromPayload(IntPtr payload) => new CacheMissReflectionFake(payload);
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class MetadataCacheHitFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public MetadataCacheHitFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // A registered metadata factory must serve this; the static-abstract member throwing proves
        // the typed cache was consulted instead of the reflection scan.
        public static TypeMetadata GetTypeMetadata()
            => throw new InvalidOperationException("static-abstract GetTypeMetadata must not run when a factory is cached");
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class MetadataCacheMissFake : ISwiftObject
    {
        // A distinctive non-zero handle the reflection fallback must find and round-trip.
        internal static readonly TypeMetadata Sentinel = TypeMetadata.FromHandle(new IntPtr(0x5AFED00D));
        public IntPtr Handle { get; }
        public MetadataCacheMissFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // No factory is registered for this type, so the reflective last resort must find and invoke
        // this static member and return its metadata.
        public static TypeMetadata GetTypeMetadata() => Sentinel;
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class SeamHitFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public SeamHitFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // The shared resolution seam must serve a registered factory; the throwing static-abstract
        // member proves the typed cache was consulted instead of the reflection scan.
        public static TypeMetadata GetTypeMetadata()
            => throw new InvalidOperationException("static-abstract GetTypeMetadata must not run when a factory is cached");
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class SeamMissFake : ISwiftObject
    {
        internal static readonly TypeMetadata Sentinel = TypeMetadata.FromHandle(new IntPtr(0x5EA31115));
        public IntPtr Handle { get; }
        public SeamMissFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // No factory registered → the shared seam falls through to the reflective last resort.
        public static TypeMetadata GetTypeMetadata() => Sentinel;
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class UncachedEntryFake : ISwiftObject
    {
        public IntPtr Handle { get; }
        public UncachedEntryFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // Drives the by-Type public entry (TypeMetadata.TryGetTypeMetadata<T>) into the uncached
        // resolver; the throwing static-abstract member proves the cache-first seam was used.
        public static TypeMetadata GetTypeMetadata()
            => throw new InvalidOperationException("static-abstract GetTypeMetadata must not run when a factory is cached");
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    private sealed class RegisterBothFake : ISwiftObject
    {
        internal static readonly TypeMetadata Sentinel = TypeMetadata.FromHandle(new IntPtr(0x80F11234));
        public IntPtr Handle { get; }
        public RegisterBothFake(IntPtr handle) => Handle = handle;
        public void Dispose() { }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        // RegisterSwiftObjectFactory<T> wires both dispatchers to these concrete members.
        public static TypeMetadata GetTypeMetadata() => Sentinel;
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
        public static ISwiftObject NewFromPayload(IntPtr payload) => new RegisterBothFake(payload);
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
        public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Adopt;
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
    public void SwiftObjectHelper_GetTypeMetadata_CacheHit_UsesFactoryNotReflection()
    {
        var sentinel = TypeMetadata.FromHandle(new IntPtr(0x7E570001));
        TypeMetadataDispatcher.Register(typeof(MetadataCacheHitFake), () => sentinel);

        // The typed factory must serve this without invoking the throwing static-abstract member.
        var result = SwiftObjectHelper<MetadataCacheHitFake>.GetTypeMetadata();

        Assert.Equal(sentinel.Handle, result.Handle);
    }

    [Fact]
    public void SwiftObjectHelper_GetTypeMetadata_CacheMiss_FallsBackToReflection()
    {
        // No factory registered → the reflective last resort must find the static member and
        // round-trip its metadata.
        var result = SwiftObjectHelper<MetadataCacheMissFake>.GetTypeMetadata();

        Assert.Equal(MetadataCacheMissFake.Sentinel.Handle, result.Handle);
    }

    [Fact]
    public void ResolveTypeMetadataCacheFirst_CacheHit_UsesFactoryNotReflection()
    {
        var sentinel = TypeMetadata.FromHandle(new IntPtr(0x7E570002));
        TypeMetadataDispatcher.Register(typeof(SeamHitFake), () => sentinel);

        // The seam every non-AOT metadata lookup shares — SwiftObjectHelper, the by-Type uncached
        // resolver, and CreateAnyRuntime — must serve the typed factory without reflecting.
        var result = SwiftObjectReflectionHelper.ResolveTypeMetadataCacheFirst(typeof(SeamHitFake));

        Assert.Equal(sentinel.Handle, result.Handle);
    }

    [Fact]
    public void ResolveTypeMetadataCacheFirst_CacheMiss_FallsBackToReflection()
    {
        // No factory registered → the shared seam falls through to the reflective last resort.
        var result = SwiftObjectReflectionHelper.ResolveTypeMetadataCacheFirst(typeof(SeamMissFake));

        Assert.Equal(SeamMissFake.Sentinel.Handle, result.Handle);
    }

    [Fact]
    public void TryGetTypeMetadata_Uncached_CacheHit_UsesFactory()
    {
        var sentinel = TypeMetadata.FromHandle(new IntPtr(0x7E570003));
        TypeMetadataDispatcher.Register(typeof(UncachedEntryFake), () => sentinel);

        // Public by-Type entry: a fresh type misses the outer TypeMetadata.Cache and reaches the
        // uncached resolver, which must resolve cache-first instead of invoking the throwing member.
        var found = TypeMetadata.TryGetTypeMetadata<UncachedEntryFake>(out var result);

        Assert.True(found);
        Assert.Equal(sentinel.Handle, result!.Value.Handle);
    }

    [Fact]
    public void RegisterSwiftObjectFactory_RegistersMetadataAndPayloadDispatchers()
    {
        SwiftMarshal.RegisterSwiftObjectFactory<RegisterBothFake>();

        // The public registration API the generator + SwiftFrameworkResolver call must populate
        // BOTH the metadata and the NewFromPayload dispatchers from one call.
        Assert.True(TypeMetadataDispatcher.TryGet(typeof(RegisterBothFake), out var metadata));
        Assert.Equal(RegisterBothFake.Sentinel.Handle, metadata.Handle);

        var created = NewFromPayloadDispatcher.TryCreate(typeof(RegisterBothFake), new IntPtr(0xC0DE));
        var typed = Assert.IsType<RegisterBothFake>(created);
        Assert.Equal(new IntPtr(0xC0DE), typed.Handle);
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
