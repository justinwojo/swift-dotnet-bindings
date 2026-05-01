// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Numerics;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Exercises the relaxed `where T : ISwiftObject` constraint for generic types
/// whose generic parameter has no protocol conformances. Mirrors the
/// RealityFoundation MeshBuffer&lt;TElement&gt; / UnsafeForceEffectBuffer&lt;T&gt;
/// shape that previously failed compilation with CS0315 when instantiated with
/// blittable types like Vector3, Quaternion, float, or uint.
///
/// Each test calls a Swift @_cdecl factory that returns a
/// `BlittableElementBuffer&lt;T&gt;` for a non-ISwiftObject T, then reads back
/// its members through additional @_cdecl getters. The compile gate alone proves
/// the where-clause relaxation works; the runtime assertions prove the
/// unconstrained `TypeMetadata.GetTypeMetadataOrThrow&lt;T&gt;()` source resolves
/// metadata correctly for these instantiations.
/// </summary>
public class BlittableGenericArgsTests : TestBase
{
    public BlittableGenericArgsTests(TestResults results) : base(results) { }

    public void TestFloatBufferRoundTrip()
    {
        var buffer = Functions.MakeFloatBuffer(value: 1.5f, count: 7);
        var value = Functions.FloatBufferValue(buffer);
        var count = Functions.FloatBufferCount(buffer);
        AssertEqual(1.5f, value, "BlittableElementBuffer<Float>.Value");
        AssertEqual(7, count, "BlittableElementBuffer<Float>.Count");
        TestLogger.Info($"BlittableElementBuffer<Float>(1.5, 7) = ({value}, {count})");
    }

    public void TestUInt32BufferRoundTrip()
    {
        var buffer = Functions.MakeUInt32Buffer(value: 0xDEADBEEFu, count: 3);
        var value = Functions.Uint32BufferValue(buffer);
        var count = Functions.Uint32BufferCount(buffer);
        AssertEqual(0xDEADBEEFu, value, "BlittableElementBuffer<UInt32>.Value");
        AssertEqual(3, count, "BlittableElementBuffer<UInt32>.Count");
        TestLogger.Info($"BlittableElementBuffer<UInt32>(0xDEADBEEF, 3) = ({value:X}, {count})");
    }

    public void TestSimdFloat3BufferRoundTrip()
    {
        // simd_float3 projects to System.Numerics.Vector3 — proves the SIMD
        // metadata accessor (TryGetNumericsMetadata → SBW_simd_float3_GetMetadata)
        // resolves correctly when the surrounding generic drops ISwiftObject.
        var buffer = Functions.MakeFloat3Buffer(x: 1f, y: 2f, z: 3f, count: 5);
        var sum = Functions.SumFloat3BufferValue(buffer);
        var count = Functions.Float3BufferCount(buffer);
        AssertEqual(6f, sum, "BlittableElementBuffer<simd_float3> component sum");
        AssertEqual(5, count, "BlittableElementBuffer<simd_float3>.Count");
        TestLogger.Info($"BlittableElementBuffer<simd_float3>((1,2,3), 5) sum={sum}, count={count}");
    }

    public void TestSimdQuatfBufferRoundTrip()
    {
        // simd_quatf projects to System.Numerics.Quaternion. The W (real)
        // component must survive the metadata round trip.
        var buffer = Functions.MakeQuatfBuffer(x: 0.1f, y: 0.2f, z: 0.3f, w: 0.4f, count: 2);
        var realPart = Functions.QuatfBufferRealPart(buffer);
        var count = Functions.QuatfBufferCount(buffer);
        AssertEqual(0.4f, realPart, "BlittableElementBuffer<simd_quatf>.value.real");
        AssertEqual(2, count, "BlittableElementBuffer<simd_quatf>.Count");
        TestLogger.Info($"BlittableElementBuffer<simd_quatf>(real={realPart}, count={count})");
    }
}
