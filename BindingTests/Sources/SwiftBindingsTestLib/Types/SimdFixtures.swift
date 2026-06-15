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

// MARK: - Async + SIMD Fixtures
//
// Exercises the WrapperEmitter.Async.cs SIMD bound-generic wedge. PInvokeEmitter
// routes Swift.SIMD2/3/4<Float> through CdeclFrozenStruct (IntPtr), so the async
// heap-buffer path MUST emit the matching `{name}Ptr` local — otherwise the
// generated async wrapper call-site references an undefined identifier.

public func asyncSumFloat3(_ v: simd_float3) async -> Float {
    return v.x + v.y + v.z
}

public func asyncEchoFloat3(_ v: simd_float3) async -> simd_float3 {
    return v
}

public func asyncEchoFloat4(_ v: simd_float4) async -> simd_float4 {
    return v
}

public func asyncEchoFloat4x4(_ m: simd_float4x4) async -> simd_float4x4 {
    return m
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

// MARK: - Multi-SIMD Constructor With Default Arguments (RealityKit.Transform shape)
//
// Mirrors RealityKit's `@inlinable public Transform.init(scale:rotation:translation:)`:
// a type whose initializer takes several SIMD parameters (a mix of bound-generic
// `SIMD3<Float>` and the C-imported `simd_quatf`) where every parameter carries a Swift
// default. RealityKit.Transform is a STRUCT, and the faithful `@inlinable` repro lives on
// `SimdDefaultCtorStruct` below (Swift forbids `@inlinable` designated inits on a class).
// This class is a positive control: a plain-public multi-SIMD default-arg class init must
// wrap fine through the indirect (pointer) path the property setters already use.
public class SimdDefaultCtorHolder {
    public var scale: simd_float3
    public var rotation: simd_quatf
    public var translation: simd_float3

    public init(scale: simd_float3 = simd_float3(1, 1, 1),
                rotation: simd_quatf = simd_quatf(ix: 0, iy: 0, iz: 0, r: 1),
                translation: simd_float3 = simd_float3(0, 0, 0)) {
        self.scale = scale
        self.rotation = rotation
        self.translation = translation
    }

    public func describe() -> String {
        return "\(scale.x),\(scale.y),\(scale.z)|\(rotation.imag.x),\(rotation.imag.y),\(rotation.imag.z),\(rotation.real)|\(translation.x),\(translation.y),\(translation.z)"
    }
}

// Non-@frozen (resilient, library-evolution) struct — the generator emits it as
// ClassWithOpaquePayload (a C# class backed by a SafeHandle, returned via SwiftIndirectResult),
// the same C# shape it gives RealityKit.Transform.
//
// Crash repro: multiple SIMD parameters passed BY VALUE. The single-param
// `init(basis: simd_float4x4)` ctor marshals its argument through a buffer pointer (`@_cdecl`
// CallConvCdecl wrapper) and works. But `init(scale:rotation:translation:)` — three SIMD values
// (a mix of bound-generic `SIMD3<Float>` and the C-imported `simd_quatf`) — binds directly to the
// real Swift init symbol via `CallConvSwift` with each value passed by register. Mono's JIT lane
// cannot lower the multi-word SIMD register ABI for this call and throws `InvalidProgramException`.
// The fix routes these SIMD ctor params through the same indirect (pointer/buffer) path the SIMD
// property setters and the `init(basis:)` ctor already use.
public struct SimdDefaultCtorStruct {
    public var scale: simd_float3
    public var rotation: simd_quatf
    public var translation: simd_float3

    // Multiple SIMD params by value — the crash shape (mirrors Transform(scale:rotation:translation:)).
    public init(scale: simd_float3, rotation: simd_quatf, translation: simd_float3) {
        self.scale = scale
        self.rotation = rotation
        self.translation = translation
    }

    // Note on the parser half of the fix: the real RealityKit.Transform.init is `@inlinable
    // public`, which swift-api-digester records (for system frameworks) as declAttributes
    // ['Inlinable'] with NO AccessControl — the signal that made the parser mis-classify it as
    // module-internal and drop the @_cdecl wrapper. That exact ABI shape is NOT reproducible from a
    // fixture here: our own library-evolution build always emits an explicit AccessControl attribute
    // for public members, so an `@inlinable public init` in this file records as
    // ['AccessControl', 'Inlinable'] and never trips the heuristic. The parser fix is therefore
    // covered by the SwiftABIParser unit test (which constructs the no-AccessControl node directly)
    // and by RealityFoundation consumer validation. This fixture covers the emitter half: a multi-SIMD
    // ctor on a ClassWithOpaquePayload type must route its params through the indirect (pointer) path.

    // Single SIMD param routed through a buffer pointer — the control that already wraps correctly
    // (mirrors Transform(matrix:)). Confirms the indirect path is sound for SIMD ctor params.
    public init(basis: simd_float4x4) {
        self.scale = simd_float3(basis.columns.0.x, basis.columns.1.y, basis.columns.2.z)
        self.rotation = simd_quatf(basis)
        self.translation = simd_float3(basis.columns.3.x, basis.columns.3.y, basis.columns.3.z)
    }

    public func describe() -> String {
        return "\(scale.x),\(scale.y),\(scale.z)|\(rotation.real)|\(translation.x),\(translation.y),\(translation.z)"
    }
}

// MARK: - Mixed-bindability SIMD enum payload (RealityFoundation MaterialParameters.Value shape)
//
// Mirrors RealityFoundation's `MaterialParameters.Value`: an enum whose associated-value
// cases mix a BINDABLE simd matrix (`simd_float4x4` → System.Numerics.Matrix4x4) with one
// that has NO managed equivalent (`simd_float2x2` — System.Numerics has no 2×2 matrix) plus
// a plain scalar case. The 2×2 case must resolve to a direct `Swift.AnyType` payload and be
// cleanly SKIPPED — no factory, no TryGet, and critically no reference to the C `simd`
// namespace — while the bindable 4×4 case and the scalar case still emit and round-trip.
//
// This is the in-repo durable gate for the regression where `simd_float2x2` was mis-routed
// through ObjC auto-bridging and the enum case emitted an undefined `simd.simd_float2x2`
// reference (CS0246 at consumer compile, exactly the RealityFoundation pre-flight failure).
// The compile gate (`nuke binding-tests --compile-only`) catches a reverted fix: if the
// `simd` module's `"valueTypesOnly": true` flag is dropped from apple-frameworks.json, every
// simd type not hand-listed re-bridges through ObjC and this enum re-emits an undefined
// `simd.simd_float2x2`, failing to compile.
//
// Why this is reproducible now (it was previously documented as unreproducible in
// DirectAnyTypePayloadSkip.swift): the `simd` module is flagged `valueTypesOnly`, so
// simd_float2x2 is a KNOWN value type and is NOT ObjC-auto-bridged; and it has no
// SimdDatabase.xml managed binding, so it falls through to AnyType — the exact direct-
// AnyType single-payload shape the `HasUnsupportedAnyTypeInPayload` skip-gate targets.
// (valueTypesOnly is the module-level successor to the old per-type `valueTypes` allow-list,
// which omitted simd_float2x2 and other simd permutations such as the integer/packed vectors.)
public enum SimdMatrixPayload {
    /// No managed 2×2 equivalent → direct `Swift.AnyType` payload → case is skipped.
    case affine2x2(simd_float2x2)
    /// Bindable: `simd_float4x4` → `System.Numerics.Matrix4x4`.
    case transform4x4(simd_float4x4)
    /// Plain scalar companion case — must keep emitting alongside the skipped 2×2 case.
    case untextured(Bool)
}

/// Builds the bindable `.transform4x4` case from a 4×4 — lets the C# side construct a
/// payload whose case factory the generator could not synthesize from a managed type.
public func makeTransform4x4Payload(_ m: simd_float4x4) -> SimdMatrixPayload {
    return .transform4x4(m)
}

/// Extracts the 4×4 transform's diagonal as a vector (all-zeros for other cases), so the
/// C# side can verify the bindable `simd_float4x4` case survived the round-trip.
public func transform4x4Diagonal(_ payload: SimdMatrixPayload) -> simd_float4 {
    switch payload {
    case .transform4x4(let m):
        return simd_float4(m.columns.0.x, m.columns.1.y, m.columns.2.z, m.columns.3.w)
    default:
        return simd_float4(0, 0, 0, 0)
    }
}

/// Returns the Bool of the `.untextured` case (false otherwise) — proves the scalar
/// companion case still round-trips alongside the skipped 2×2 case.
public func untexturedFlag(_ payload: SimdMatrixPayload) -> Bool {
    switch payload {
    case .untextured(let flag): return flag
    default: return false
    }
}

// Control: the same multi-SIMD initializer WITHOUT default arguments. Isolates whether
// the wrapper-bypass is triggered by the SIMD parameter types or by the default args.
public class SimdNoDefaultCtorHolder {
    public var scale: simd_float3
    public var rotation: simd_quatf

    public init(scale: simd_float3, rotation: simd_quatf) {
        self.scale = scale
        self.rotation = rotation
    }

    public func describe() -> String {
        return "\(scale.x),\(scale.y),\(scale.z)|\(rotation.real)"
    }
}
