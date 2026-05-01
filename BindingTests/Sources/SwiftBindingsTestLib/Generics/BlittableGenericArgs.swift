// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import simd

// MARK: - Blittable-T Generic Fixtures
//
// Mirrors RealityFoundation's `MeshBuffer<TElement>` shape — a public generic
// struct whose type parameter has NO protocol conformances and whose concrete
// instantiations are blittable C# types (Vector3, Quaternion, float, uint).
//
// Before relaxing the unconditional `where T : ISwiftObject` constraint, the
// generator emitted `BlittableElementBuffer<T> where T : ISwiftObject`, and any
// instantiation with a non-ISwiftObject type (Vector3, simd_quatf, float, uint)
// failed at the call site with CS0315. The generator now drops the ISwiftObject
// seed when a generic param has no protocol conformances, so these fixtures
// compile end-to-end and we can prove the runtime metadata source works for the
// relaxed instantiations.

/// Generic struct with no protocol-conformance constraint on T. Holds a single
/// element + a count and exposes operations that require the binding to look up
/// type metadata for `T` at the @_cdecl boundary.
public struct BlittableElementBuffer<T> {
    public let value: T
    public let count: Int32

    public init(value: T, count: Int32) {
        self.value = value
        self.count = count
    }

    /// Returns the stored value — exercises returning a generic-T value.
    public func first() -> T {
        return value
    }
}

// MARK: - Concrete Producers
//
// The element-typed generic surface itself is the compile gate; producing
// values from the Swift side gives the runtime tests a payload they can read
// back without needing to construct `BlittableElementBuffer<simd_float3>`
// from C# (which has its own marshalling concerns covered by other fixtures).

/// Constructs a `BlittableElementBuffer<simd_float3>` — exercises generic
/// metadata accessor with `simd_float3` (blittable, non-ISwiftObject).
public func makeFloat3Buffer(x: Float, y: Float, z: Float, count: Int32) -> BlittableElementBuffer<simd_float3> {
    return BlittableElementBuffer<simd_float3>(value: simd_float3(x, y, z), count: count)
}

/// Constructs a `BlittableElementBuffer<simd_quatf>` — exercises generic
/// metadata accessor with `simd_quatf` (Quaternion).
public func makeQuatfBuffer(x: Float, y: Float, z: Float, w: Float, count: Int32) -> BlittableElementBuffer<simd_quatf> {
    return BlittableElementBuffer<simd_quatf>(value: simd_quatf(ix: x, iy: y, iz: z, r: w), count: count)
}

/// Constructs a `BlittableElementBuffer<Float>` — exercises generic
/// metadata accessor with Swift.Float (blittable primitive).
public func makeFloatBuffer(value: Float, count: Int32) -> BlittableElementBuffer<Float> {
    return BlittableElementBuffer<Float>(value: value, count: count)
}

/// Constructs a `BlittableElementBuffer<UInt32>` — exercises generic
/// metadata accessor with Swift.UInt32 (blittable primitive).
public func makeUInt32Buffer(value: UInt32, count: Int32) -> BlittableElementBuffer<UInt32> {
    return BlittableElementBuffer<UInt32>(value: value, count: count)
}

/// Sums all components of a buffer's stored simd_float3 — proves the value
/// survives the round trip through the relaxed-constraint generic.
public func sumFloat3BufferValue(_ buffer: BlittableElementBuffer<simd_float3>) -> Float {
    return buffer.value.x + buffer.value.y + buffer.value.z
}

/// Reads the stored simd_quatf's real component — same purpose as
/// `sumFloat3BufferValue` but for Quaternion.
public func quatfBufferRealPart(_ buffer: BlittableElementBuffer<simd_quatf>) -> Float {
    return buffer.value.real
}

/// Returns the stored Float value.
public func floatBufferValue(_ buffer: BlittableElementBuffer<Float>) -> Float {
    return buffer.value
}

/// Returns the stored UInt32 value.
public func uint32BufferValue(_ buffer: BlittableElementBuffer<UInt32>) -> UInt32 {
    return buffer.value
}

/// Returns the stored count from any specialization — confirms the
/// non-generic Int32 field is reachable through every instantiation.
public func float3BufferCount(_ buffer: BlittableElementBuffer<simd_float3>) -> Int32 {
    return buffer.count
}

public func quatfBufferCount(_ buffer: BlittableElementBuffer<simd_quatf>) -> Int32 {
    return buffer.count
}

public func floatBufferCount(_ buffer: BlittableElementBuffer<Float>) -> Int32 {
    return buffer.count
}

public func uint32BufferCount(_ buffer: BlittableElementBuffer<UInt32>) -> Int32 {
    return buffer.count
}
