// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import simd

// MARK: - Bare-Metadata-Probe Generic Fixture (NativeAOT rooting)
//
// Mirrors RealityFoundation's `MeshBuffers.Semantic<TElement>` shape AND, more
// importantly, the way its per-package test reaches that type: a bare
// `SwiftObjectHelper<Closed>.GetTypeMetadata()` metadata probe with NO call that
// returns the closed instantiation in the consuming app.
//
// This is the case `BlittableElementBuffer<T>` does NOT cover. Both are
// non-frozen (resilient, library-evolution) generic structs that project to a
// C# reference-type class with a SafeHandle payload — so on NativeAOT both use
// canonical/shared generic code, and the metadata accessor dispatches through a
// constrained static-abstract `T.GetTypeMetadata()`. The difference is the entry
// path: `BlittableElementBuffer` is reached through a factory function that
// RETURNS the closed form (the return/marshal path roots the closed type), so
// its metadata specialization is always code-generated. The closed form here is
// reached ONLY through the bare `SwiftObjectHelper<...>.GetTypeMetadata()` probe
// in the consuming test — exactly the path the RealityFoundation per-package
// test takes, which is skipped on the NativeAOT lane today.
//
// A producer that returns the closed `<simd_float3>` form exists so the
// generator RECORDS the closed instantiation (module-init factory registration).
// The producer is intentionally NOT called from any C# test, so the closed type
// is reachable at runtime only via the factory registration and the bare metadata
// probe — isolating the metadata accessor's specialization from the
// return/marshal rooting path. The probe passing on NativeAOT confirms the
// closed instantiation's `...VMa` Swift metadata accessor is reachable without a
// producer call masking it (the consuming reference alone roots it).

/// Non-frozen generic struct (resilient → C# class with a SafeHandle payload),
/// matching the `MeshBuffers.Semantic<TElement>` projection. `T` has no
/// protocol conformances, so the generator drops the `ISwiftObject` seed and the
/// type instantiates with a blittable SIMD element.
public struct AotRootedMetadataBuffer<T> {
    public let value: T

    public init(value: T) {
        self.value = value
    }

    /// Returns the stored value — gives the type a generic-T member so its
    /// metadata accessor is exercised, matching the buffer shapes upstream.
    public func first() -> T {
        return value
    }
}

/// Constructs an `AotRootedMetadataBuffer<simd_float3>`. Its only purpose is to
/// make the generator record the closed `<simd_float3>` instantiation (factory
/// registration). It is deliberately never called from C#, so the closed form is
/// reached at runtime solely through the bare metadata probe — mirroring the
/// RealityFoundation per-package probe.
public func makeAotRootedFloat3Buffer(x: Float, y: Float, z: Float) -> AotRootedMetadataBuffer<simd_float3> {
    return AotRootedMetadataBuffer<simd_float3>(value: simd_float3(x, y, z))
}
