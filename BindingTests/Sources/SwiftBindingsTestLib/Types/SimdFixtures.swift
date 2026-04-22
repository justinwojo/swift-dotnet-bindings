// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import simd

// MARK: - SIMD Projection Fixtures (Issue 2)
//
// These fixtures exercise the Swift `simd` module bound-generic projections onto
// `System.Numerics.Vector2/3/4` and `Matrix4x4`. Swift `SIMD2/3/4<Float>` is a
// typealias for `simd_float2/3/4`, which is bit-compatible with the corresponding
// managed numerics type at the ABI boundary.
//
// See: `src/Swift.Runtime/src/Swift/SimdDatabase.xml` and the
// `BoundGenericSimdAliases` dictionary in `TypeDatabaseExtensions.cs`.

/// Free functions covering `simd_float2/3/4` param + return.
public func makeFloat2(x: Float, y: Float) -> simd_float2 {
    return simd_float2(x, y)
}

public func makeFloat3(x: Float, y: Float, z: Float) -> simd_float3 {
    return simd_float3(x, y, z)
}

public func makeFloat4(x: Float, y: Float, z: Float, w: Float) -> simd_float4 {
    return simd_float4(x, y, z, w)
}

/// Echoes (round-trips) a SIMD2 param through the @_cdecl boundary.
public func echoFloat2(_ v: simd_float2) -> simd_float2 {
    return v
}

public func echoFloat3(_ v: simd_float3) -> simd_float3 {
    return v
}

public func echoFloat4(_ v: simd_float4) -> simd_float4 {
    return v
}

/// Sums components — proves field values survived the crossing.
public func sumFloat2(_ v: simd_float2) -> Float {
    return v.x + v.y
}

public func sumFloat3(_ v: simd_float3) -> Float {
    return v.x + v.y + v.z
}

public func sumFloat4(_ v: simd_float4) -> Float {
    return v.x + v.y + v.z + v.w
}

// MARK: - simd_float4x4

/// Builds a 4×4 matrix from 4 column vectors.
public func makeFloat4x4(
    _ col0: simd_float4,
    _ col1: simd_float4,
    _ col2: simd_float4,
    _ col3: simd_float4
) -> simd_float4x4 {
    return simd_float4x4(columns: (col0, col1, col2, col3))
}

/// Identity matrix constant — simplest fixture to round-trip.
public func identityFloat4x4() -> simd_float4x4 {
    return matrix_identity_float4x4
}

public func echoFloat4x4(_ m: simd_float4x4) -> simd_float4x4 {
    return m
}

/// Returns the diagonal as a simd_float4 — exercises a getter on an opaque
/// column-major matrix in a way consumers can actually inspect.
public func diagonalFloat4x4(_ m: simd_float4x4) -> simd_float4 {
    return simd_float4(m.columns.0.x, m.columns.1.y, m.columns.2.z, m.columns.3.w)
}

// MARK: - Container / Property Fixtures

/// Class with SIMD-typed stored properties, exercising property getters/setters
/// with projected System.Numerics types.
public class TransformHolder {
    public var position: simd_float3
    public var color: simd_float4
    public var basis: simd_float4x4

    public init(position: simd_float3, color: simd_float4) {
        self.position = position
        self.color = color
        self.basis = matrix_identity_float4x4
    }
}
