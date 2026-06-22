// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async generic-bridge result-carrier release (AsyncMethodGenericBridgeEmitter)
//
// A method-level generic parameter that is CLASS-BOUND (its constraint protocol refines AnyObject)
// routes an async method through the async GENERIC bridge — a separate emission of the
// result-carrier release algebra from the non-generic async harness. The bridge opens the conformer
// via `Unmanaged<AnyObject>.fromOpaque`, so the constraint must be class-bound; a value-type
// `Collection`/`Equatable` constraint is NOT eligible and routes elsewhere.
//
// The bridge writes the result into a carrier via `initializeMemory(as: T.self, repeating: result,
// count: 1)` (the type's copy witness — a +1 on any internal references). The C# completion callback
// must release that +1 before `SBW_Free` reclaims the raw allocation, exactly as the non-generic
// harness does. These fixtures exercise the bridge's ComplexValue return arms with
// LifetimeTracker-counted payloads so a leaked carrier +1 surfaces as a non-zero live count.

/// Class-bound marker so the method-level generic param routes through the async generic bridge.
public protocol MapperSeed: AnyObject {}

/// Heap conformer carrying the element count the bridge fixtures map from.
public final class CountSeed: MapperSeed {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

/// Host for `static`, `async`, generic-over-a-class-bound-protocol functions whose returns exercise
/// the async generic bridge's result-carrier release arms.
public struct GenericBridgeReturns {
    /// Non-frozen (resilient) struct return through the generic bridge. The struct embeds a
    /// LifetimeTracker-counted `TrackedRef`; the bridge's callback-owns-the-carrier arm must
    /// value-witness-Destroy the carrier so the embedded ref's +1 is released.
    public static func wrap<Seed>(seed: Seed) async -> TrackedRefStruct where Seed: MapperSeed {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return TrackedRefStruct(value: (seed as? CountSeed)?.value ?? 0)
    }

    /// Frozen-struct-with-ref-fields return through the generic bridge. `FrozenTrackedRefStruct`
    /// projects to the ClassWithBufferStruct path, so the bridge's completion callback takes the
    /// SEPARATE `carrierNeedsDestroy` arm (distinct from `wrap`'s non-frozen ClassWithOpaquePayload
    /// arm): `NewFromPayload` runs its own copy into a managed buffer, and the callback must still
    /// value-witness-Destroy the original carrier so the embedded `TrackedRef` +1 is released.
    public static func wrapFrozen<Seed>(seed: Seed) async -> FrozenTrackedRefStruct where Seed: MapperSeed {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return FrozenTrackedRefStruct(value: (seed as? CountSeed)?.value ?? 0)
    }

    /// Negative regression guard: a `String` return must NOT ride the generic bridge's value-carrier
    /// (ComplexValue) ABI. `String` projects publicly to `string` (not an `ISwiftObject`), so the
    /// bridge's callback arms would not compile and could not release the carrier's String storage.
    /// `ClassifyReturnKind` bails on `Swift.String`, so the generic bridge does not engage its
    /// value-carrier wrapper for this return — the guard is exactly that the bail leaves no
    /// broken/leaky bridge emission and the compile gate stays green (it does not assert that some
    /// other path then produces a working binding). The returned string is forced past the
    /// small-string inline threshold so that, had the carrier ABI wrongly engaged, the backing
    /// storage would be genuinely heap-backed — the case that would actually leak.
    public static func describe<Seed>(seed: Seed) async -> String where Seed: MapperSeed {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let n = (seed as? CountSeed)?.value ?? -1
        return "generic-bridge-string-return-payload-well-past-the-inline-threshold-\(n)"
    }
}
