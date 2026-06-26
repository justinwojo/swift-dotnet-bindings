// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class TypeMetadataTests : IClassFixture<TypeMetadataTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public TypeMetadataTests(TestFixture fixture)
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

    static TypeMetadata MakePhonyMetadata(int value)
    {
        IntPtr p = new IntPtr(value);

        var t = typeof(TypeMetadata);
        var ci = t.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new Type[] { typeof(IntPtr) })!;

        return (TypeMetadata)(ci.Invoke(new object[] { p }));
    }

    [Fact]
    public static void CacheWorks()
    {
        var fakeMeta = MakePhonyMetadata(42);
        TypeMetadata.Cache.GetOrAdd(typeof(System.Convert), (t) =>
        {
            return fakeMeta;
        });
        Assert.True(TypeMetadata.Cache.TryGet(typeof(System.Convert), out var result));
    }

    [Fact]
    public static void TryGetFail()
    {
        var contains = TypeMetadata.Cache.TryGet(typeof(System.EventArgs), out var result);
        Assert.False(contains);
    }

    [Fact]
    public static void TryGetSucceed()
    {
        var fakeMeta = MakePhonyMetadata(43);
        TypeMetadata.Cache.GetOrAdd(typeof(System.Random), (t) =>
        {
            return fakeMeta;
        });
        var contains = TypeMetadata.Cache.TryGet(typeof(System.Random), out var result);
        Assert.True(contains);
    }

    public struct ThisOnlyGetsUsedHere : ISwiftObject
    {
        static TypeMetadata ISwiftObject.GetTypeMetadata()
        {
            if (TypeMetadata.TryGetTypeMetadata<int>(out var fakeMd))
            {
                return TypeMetadata.Cache.GetOrAdd(typeof(ThisOnlyGetsUsedHere), (type) => fakeMd.Value);
            }
            return TypeMetadata.Zero;
        }

        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            return 0;
        }

        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
        {
            return new ThisOnlyGetsUsedHere();
        }

        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<U>()
        {
            return ProtocolConformanceDescriptor.Zero;
        }

        // Value-type ISwiftObject: short-circuits to Inline before this is read (inert, declared honestly).
        static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
            => global::Swift.Runtime.PayloadConstructionSemantics.Inline;

        public void Dispose() { }
    }

    [Fact]
    public static void CanTryGetMetadata()
    {
        Assert.True(TypeMetadata.TryGetTypeMetadata<ThisOnlyGetsUsedHere>(out var md));
    }

    [Fact]
    public static void CannotTryGetMetadata()
    {
        Assert.False(TypeMetadata.TryGetTypeMetadata<TypeMetadataTests>(out var md));
    }

    [Fact]
    public static void TryGetWillThrow()
    {
        Assert.Throws<SwiftRuntimeException>(() =>
        {
            TypeMetadata.GetTypeMetadataOrThrow<TypeMetadataTests>();
        });
    }

    [Fact]
    public static void CanTryGetOnInstance()
    {
        Assert.True(TypeMetadata.TryGetTypeMetadata<ThisOnlyGetsUsedHere>(out var md));
    }

    [Fact]
    public static void CannotGetOnUnknownInstance()
    {
        Assert.False(TypeMetadata.TryGetTypeMetadata<object>(out var md));
    }

    [Fact(Skip = "TypeMetadata cache can return wrong values - GetOrAdd for existential containers has inconsistent caching behavior")]
    public static void FailsWhenMetadataIsNotValid()
    {
        Assert.False(TypeMetadata.TryGetTypeMetadata<AnyTypeMock>(out var md));
    }

    // Tests for reflection-based metadata lookup (Mono JIT workaround)

    [Fact]
    public static void SwiftObjectHelper_GetTypeMetadata_UsesReflection()
    {
        // SwiftObjectHelper<T>.GetTypeMetadata() should work via reflection path
        // (avoids T.GetTypeMetadata() static virtual dispatch that crashes Mono JIT)
        var metadata = SwiftObjectHelper<ThisOnlyGetsUsedHere>.GetTypeMetadata();
        Assert.True(metadata.IsValid);
    }

    [Fact]
    public static void SwiftObjectHelper_GetTypeMetadata_Cached()
    {
        // Second call should use cache
        var metadata1 = SwiftObjectHelper<ThisOnlyGetsUsedHere>.GetTypeMetadata();
        var metadata2 = SwiftObjectHelper<ThisOnlyGetsUsedHere>.GetTypeMetadata();
        Assert.Equal(metadata1, metadata2);
    }

    [Fact]
    public static void SwiftObjectHelper_NewFromPayload_UsesReflection()
    {
        // NewFromPayload should work via reflection path
        var obj = SwiftObjectHelper<ThisOnlyGetsUsedHere>.NewFromPayload(IntPtr.Zero);
        Assert.IsType<ThisOnlyGetsUsedHere>(obj);
    }

    [Fact]
    public static void ReflectionHelper_InvokeGetTypeMetadata_FindsExplicitImpl()
    {
        // Should find the explicit ISwiftObject.GetTypeMetadata() implementation
        var metadata = SwiftObjectReflectionHelper.InvokeGetTypeMetadata(typeof(ThisOnlyGetsUsedHere));
        Assert.True(metadata.IsValid);
    }

    [Fact]
    public static void ReflectionHelper_InvokeGetTypeMetadata_ReturnsZeroForNonSwiftObject()
    {
        // Should return Zero for types without GetTypeMetadata
        var metadata = SwiftObjectReflectionHelper.InvokeGetTypeMetadata(typeof(string));
        Assert.False(metadata.IsValid);
    }

    [Fact]
    public static void ReflectionHelper_InvokeNewFromPayload_FindsExplicitImpl()
    {
        // Should find the explicit ISwiftObject.NewFromPayload() implementation
        var obj = SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(ThisOnlyGetsUsedHere), IntPtr.Zero);
        Assert.IsType<ThisOnlyGetsUsedHere>(obj);
    }

    [Fact]
    public static void ReflectionHelper_InvokeNewFromPayload_ThrowsForNonSwiftObject()
    {
        // Should throw for types without NewFromPayload
        Assert.Throws<InvalidOperationException>(() =>
            SwiftObjectReflectionHelper.InvokeNewFromPayload(typeof(string), IntPtr.Zero));
    }

    [Fact]
    public static void ReflectionHelper_InvokeGetProtocolConformanceDescriptor_FindsExplicitImpl()
    {
        // Should find the explicit ISwiftObject.GetProtocolConformanceDescriptor() implementation
        // ThisOnlyGetsUsedHere returns Zero for all protocols
        var desc = SwiftObjectReflectionHelper.InvokeGetProtocolConformanceDescriptor(
            typeof(ThisOnlyGetsUsedHere), typeof(ISwiftHashable));
        // Zero descriptor is valid result (means no conformance found)
        Assert.Equal(ProtocolConformanceDescriptor.Zero, desc);
    }

    [Fact]
    public static void TryGetTypeMetadata_UsesReflectionPath()
    {
        // TryGetTypeMetadata should successfully resolve via the reflection-based path
        // (bypasses SwiftObjectHelper<T> MakeGenericType which also crashes Mono)
        Assert.True(TypeMetadata.TryGetTypeMetadata<ThisOnlyGetsUsedHere>(out var md));
        Assert.True(md!.Value.IsValid);
    }

    // Tests for NativeAOT dual-dispatch pattern

    [Fact]
    public static void SwiftObjectHelper_GetTypeMetadata_WorksOnCurrentRuntime()
    {
        // Verifies the dual-dispatch pattern works regardless of runtime:
        // - NativeAOT: T.GetTypeMetadata() direct dispatch
        // - Mono JIT: reflection-based dispatch
        var metadata = SwiftObjectHelper<ThisOnlyGetsUsedHere>.GetTypeMetadata();
        Assert.True(metadata.IsValid);
    }

    [Fact]
    public static void SwiftObjectHelper_NewFromPayload_WorksOnCurrentRuntime()
    {
        // Verifies NewFromPayload dual-dispatch works regardless of runtime
        var obj = SwiftObjectHelper<ThisOnlyGetsUsedHere>.NewFromPayload(IntPtr.Zero);
        Assert.NotNull(obj);
        Assert.IsType<ThisOnlyGetsUsedHere>(obj);
    }

    [Fact]
    public static void RuntimeFeature_IsDynamicCodeSupported_DetectsRuntime()
    {
        // On desktop CoreCLR, IsDynamicCodeSupported is true (like Mono JIT).
        // On NativeAOT, it's false. This test documents the behavior.
        // The dual-dispatch pattern in SwiftObjectHelper uses this to choose
        // between direct dispatch (NativeAOT) and reflection (Mono JIT).
        bool isDynamic = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

        // On desktop test runner, this should be true (CoreCLR supports dynamic code)
        Assert.True(isDynamic);
    }

    // Tests for NewFromPayloadDispatcher factory bridge

    [Fact]
    public static void NewFromPayloadDispatcher_RegisterAndRetrieve()
    {
        // Simulate the constrained→unconstrained bridge:
        // 1. Register a factory from constrained context (as SwiftObjectHelper<T> does on NativeAOT)
        // 2. Retrieve via unconstrained TryCreate (as MarshalFromSwift<T> does)
        Swift.Runtime.InteropServices.NewFromPayloadDispatcher.Register(
            typeof(ThisOnlyGetsUsedHere),
            handle => (object)new ThisOnlyGetsUsedHere());

        var result = Swift.Runtime.InteropServices.NewFromPayloadDispatcher.TryCreate(
            typeof(ThisOnlyGetsUsedHere), IntPtr.Zero);

        Assert.NotNull(result);
        Assert.IsType<ThisOnlyGetsUsedHere>(result);
    }

    [Fact]
    public static void NewFromPayloadDispatcher_UnregisteredTypeReturnsNull()
    {
        // Unregistered types return null — caller falls back to reflection
        var result = Swift.Runtime.InteropServices.NewFromPayloadDispatcher.TryCreate(
            typeof(TypeMetadataTests), IntPtr.Zero);

        Assert.Null(result);
    }

    // Tests for TypeMetadata.RegisterMetadata API (simple enum metadata registration)

    private enum TestColor : int
    {
        Red = 0,
        Green = 1,
        Blue = 2,
    }

    private enum TestByteEnum : byte
    {
        Low = 0,
        High = 1,
    }

    private enum UnregisteredEnum : int
    {
        A = 0,
    }

    [Fact]
    public static void RegisterMetadata_EnumBecomesResolvable()
    {
        // RegisterMetadata allows simple C# enums to have Swift metadata in the cache.
        // This is how generated module initializers register enum metadata at startup.
        Assert.True(TypeMetadata.TryGetTypeMetadata<int>(out var intMd));
        TypeMetadata.RegisterMetadata(typeof(TestColor), intMd!.Value);

        Assert.True(TypeMetadata.TryGetTypeMetadata<TestColor>(out var enumMd));
        Assert.True(enumMd!.Value.IsValid);
    }

    [Fact]
    public static void RegisterMetadata_ByteEnumBecomesResolvable()
    {
        // Byte-backed enums should also be registrable via RegisterMetadata.
        Assert.True(TypeMetadata.TryGetTypeMetadata<byte>(out var byteMd));
        TypeMetadata.RegisterMetadata(typeof(TestByteEnum), byteMd!.Value);

        Assert.True(TypeMetadata.TryGetTypeMetadata<TestByteEnum>(out var enumMd));
        Assert.True(enumMd!.Value.IsValid);
    }

    [Fact]
    public static void TryGetTypeMetadata_UnregisteredEnum_ReturnsFalse()
    {
        // Enums without explicit metadata registration should NOT resolve.
        // The underlying-type fallback was removed because it produces wrong
        // Optional<T> layout (tag-byte vs extra-inhabitant encoding).
        Assert.False(TypeMetadata.TryGetTypeMetadata<UnregisteredEnum>(out _));
    }

    // Tests for null-metadata → catchable exception (OS-gated weak-linked type backstop)
    //
    // When a Swift type's metadata accessor is weak-linked (its availability floor exceeds the
    // binary's min-OS) and resolves to null on an older OS, the TypeMetadata handle is zero.
    // Reading the value witness table off a null handle previously dereferenced a null pointer
    // (or threw a bare NullReferenceException). The generated marshalling reads .Size/.Stride/
    // .Alignment, which all route through ValueWitnessTable — so these must throw a catchable,
    // self-explanatory PlatformNotSupportedException, never a native segfault or a bare NRE.

    [Fact]
    public static void Size_OnNullMetadata_ThrowsPlatformNotSupported()
    {
        var invalid = MakePhonyMetadata(0);
        Assert.False(invalid.IsValid);
        Assert.Throws<PlatformNotSupportedException>(() => { var _ = invalid.Size; });
    }

    [Fact]
    public static void Stride_OnNullMetadata_ThrowsPlatformNotSupported()
    {
        var invalid = MakePhonyMetadata(0);
        Assert.Throws<PlatformNotSupportedException>(() => { var _ = invalid.Stride; });
    }

    [Fact]
    public static void Alignment_OnNullMetadata_ThrowsPlatformNotSupported()
    {
        var invalid = MakePhonyMetadata(0);
        Assert.Throws<PlatformNotSupportedException>(() => { var _ = invalid.Alignment; });
    }

    [Fact]
    public static unsafe void ValueWitnessTable_OnNullMetadata_ThrowsPlatformNotSupported()
    {
        var invalid = MakePhonyMetadata(0);
        Assert.Throws<PlatformNotSupportedException>(() => { var _ = invalid.ValueWitnessTable; });
    }

    [Fact]
    public static void Kind_OnNullMetadata_ThrowsPlatformNotSupported()
    {
        // Kind reads the metadata kind word, not the value witness table, but a null handle is the
        // same OS-gated/weak-linked condition — so it must surface the SAME catchable
        // PlatformNotSupportedException as Size/Stride/Alignment/ValueWitnessTable, not a divergent
        // exception type that a consumer's `catch (PlatformNotSupportedException)` would miss.
        var invalid = MakePhonyMetadata(0);
        Assert.False(invalid.IsValid);
        Assert.Throws<PlatformNotSupportedException>(() => { var _ = invalid.Kind; });
    }

    [Fact]
    public static void ConformanceDispatcher_RegisterAndRetrieve()
    {
        // Simulate conformance factory bridge
        Swift.Runtime.InteropServices.ConformanceDispatcher.Register(
            typeof(ThisOnlyGetsUsedHere), typeof(ISwiftHashable),
            () => ProtocolConformanceDescriptor.Zero);

        var result = Swift.Runtime.InteropServices.ConformanceDispatcher.TryGet(
            typeof(ThisOnlyGetsUsedHere), typeof(ISwiftHashable));

        Assert.NotNull(result);
        Assert.Equal(ProtocolConformanceDescriptor.Zero, result!.Value);
    }
}
