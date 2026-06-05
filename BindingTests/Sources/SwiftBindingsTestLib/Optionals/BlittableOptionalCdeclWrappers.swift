// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Blittable-Optional params on @_cdecl method wrappers (REMEDIATION-PLAN §6)
//
// End-to-end coverage that a small *blittable* Optional parameter (`Int32?`/`Int?`/`Double?`/
// `CGFloat?`…) is correctly DECODED — not forwarded as a bare `UnsafeRawPointer` — when its
// enclosing method is lowered to a `@_cdecl` wrapper. Each method below carries a feature that
// forces a wrapper (an `@escaping` closure; a *large* `BigPoint?` optional). In the live
// generator BOTH are claimed by MethodWrapperEmitter — they compile to `_sbw_method_<hash>`
// wrappers whose bodies decode the small optional via the tag byte:
//
//     let nOpt: Int32? = n.advanced(by: 4).load(as: UInt8.self) == 0 ? n.load(as: Int32.self) : nil
//     return obj.addOptionalWith…(nOpt, …)
//
// MethodWrapperEmitter always maps params with `omitLabels: false`, so the decode is correct and
// these round-trips pass with no source change. They are NOT a reachable repro of the §6
// fallback-branch defect: that defect lives in the `else if (useCdecl)` branch of the two
// FALLBACK emitters (`ClosureEmitter.SwiftWrapper` and `OptionalPointerWrapperEmitter`), whose
// Phase-1 gates fire only when `!UsesWrapperLibrary` — i.e. when MethodWrapperEmitter has NOT
// already claimed the method. No compilable Swift shape reaches those branches today, so the
// defect is latent: the branch previously mapped a small blittable optional with
// `omitLabels: true` (the bare-pointer shape, correct only for `_dbw_init_*` dispatch targets
// that decode internally), forwarding an un-decoded `UnsafeRawPointer` — swiftc would reject the
// wrapper, the build would strip it, and the entry point would trap. That latent branch is
// hardened to `omitLabels: false` and pinned directly by the emitter unit tests in
// `OptionalPointerWrapperTests` ("Blittable-Optional @_cdecl Decode" region). These fixtures
// remain the durable runtime gate for the MethodWrapperEmitter decode path: each is round-tripped
// with a non-nil value AND nil so a mis-decode (raw bytes, or a flipped nil branch) is caught.
// Neither method's callback carries a `SwiftError*`, so both run on the simulator (Mono JIT) and
// device (NativeAOT).

/// Large (24-byte) frozen struct → `BigPoint?` is a "large Optional" (≥ 8 bytes) that the
/// method wrapper widens to `UnsafeRawPointer`, alongside which the small `Int32?` is decoded.
public struct BigPoint {
    public var x: Int64
    public var y: Int64
    public var z: Int64
    public init(x: Int64, y: Int64, z: Int64) {
        self.x = x
        self.y = y
        self.z = z
    }
    public var sum: Int32 { Int32(x + y + z) }
}

/// Frozen struct (all stored fields are frozen value types → marshals as a C# struct), so a
/// blittable-optional method param exercises the `@_cdecl` wrapper lowering, not the
/// non-frozen class-with-handle path.
public struct BlittableOptionalBox {
    public var seed: Int32
    public init(seed: Int32) {
        self.seed = seed
    }

    /// Closure-bearing method: a small blittable `Int32?` alongside an `@escaping` closure.
    /// The method wrapper decodes the optional and adapts the closure. Invokes the closure
    /// exactly once and returns `seed + (n ?? -1)`. If `n` were mis-decoded the round-trip value
    /// would be wrong; the nil branch distinguishes a decoded `nil` from a garbage non-nil.
    public func addOptionalWithClosure(_ n: Int32?, _ onDone: @escaping () -> Void) -> Int32 {
        onDone()
        return seed + (n ?? -1)
    }

    /// Large-optional-bearing method: a small blittable `Int32?` alongside a *large* `BigPoint?`.
    /// The method wrapper decodes the small optional and widens the large one to a pointer.
    /// Returns `seed + (n ?? -1) + (big?.sum ?? 0)`.
    public func addOptionalWithLargeOptional(_ n: Int32?, _ big: BigPoint?) -> Int32 {
        return seed + (n ?? -1) + (big?.sum ?? 0)
    }
}
