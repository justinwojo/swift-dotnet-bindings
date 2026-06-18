// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Finding 59 corner 4 (architecture review §2009–2022) — runtime wrapper-marshalling coverage
// for @frozen integer structs that straddle the two calling-convention size thresholds
// (AbiSizeLimits.MaxSelfSize = 8, MaxParamSize = 16 in WrapperValidation.cs).
//
// IMPORTANT — what these fixtures do and do NOT pin. The threshold constants gate wrapper
// selection only in the generator's NO-WRAPPER FALLBACK path. In a normal binding (and always in
// BindingTests) every eligible instance method gets an @_cdecl wrapper unconditionally
// (WrapperValidation.DetermineMethodWrapperDecision returns WrapperRequired whenever
// ShouldEmitWrapper is true; the size threshold is never consulted). So ALL FOUR methods below —
// regardless of being under or over 8/16 — route through SBW_* @_cdecl wrappers and are called
// over CallConvCdecl. A runtime round-trip here therefore CANNOT observe the threshold decision
// (it can't reach the path that consults it), and these tests would still pass if MaxSelfSize /
// MaxParamSize were wrong.
//
// The actual threshold DECISION is a generator policy constant, pinned at the exact ±1 boundary
// by the emitter unit tests in EmitterTests/AbiSafetyTests.cs (self 8 → false, 9 → true; param
// 16 → false, 17 → true). That is the real instrument for corner 4.
//
// What these @frozen integer structs DO cover, and why they earn their place: the @_cdecl wrapper
// path must correctly marshal frozen integer structs of 8, 16, and 24 bytes as both `self` and as
// by-value parameters, round-tripping every field through real generated bindings on a real
// device. Each carries distinct per-field sentinels summed in the return — a dropped, zeroed, or
// transposed field changes the sum, and an over-sized struct mismarshalled by the wrapper SIGSEGVs
// on --device (NativeAOT). This is boundary-straddling-integer-struct wrapper marshalling, not
// convention selection. See AbiSizeThresholdTests.cs.
//
// Integer-only by design: a Float/Bool field would trip HasIncompatibleFields, an orthogonal
// @_cdecl gate unrelated to the integer-struct sizes exercised here.

// MARK: - 8-byte self (one Int64)

/// @frozen, exactly 8 bytes (one Int64). Exercises the @_cdecl wrapper marshalling an 8-byte
/// frozen-integer `self`, plus 16- and 24-byte by-value integer-struct params.
@frozen
public struct AbiThresholdSelf8 {
    public var a: Int64
    public init(a: Int64) { self.a = a }

    /// Returns `self.a`; a mismarshalled `self` shows up as a corrupted return.
    public func selfValue() -> Int64 { return a }

    /// 16-byte by-value param. Sums self + both param fields; any dropped field changes the result.
    public func acceptParam16(_ p: AbiThresholdParam16) -> Int64 { return a &+ p.a &+ p.b }

    /// 24-byte by-value param. Sums self + all three param fields.
    public func acceptParam24(_ p: AbiThresholdParam24) -> Int64 { return a &+ p.a &+ p.b &+ p.c }
}

// MARK: - 16-byte self (two Int64)

/// @frozen, 16 bytes (two Int64). Exercises the @_cdecl wrapper marshalling a multi-word frozen
/// `self`; round-tripping the sum proves both self words survive.
@frozen
public struct AbiThresholdSelf16 {
    public var a: Int64
    public var b: Int64
    public init(a: Int64, b: Int64) { self.a = a; self.b = b }

    public func selfValue() -> Int64 { return a &+ b }
}

// MARK: - Integer-struct value params of 16 and 24 bytes

/// @frozen, exactly 16 bytes (two Int64). Used as a by-value param (see AbiThresholdSelf8.acceptParam16).
@frozen
public struct AbiThresholdParam16 {
    public var a: Int64
    public var b: Int64
    public init(a: Int64, b: Int64) { self.a = a; self.b = b }
}

/// @frozen, 24 bytes (three Int64). Used as a by-value param (see AbiThresholdSelf8.acceptParam24).
@frozen
public struct AbiThresholdParam24 {
    public var a: Int64
    public var b: Int64
    public var c: Int64
    public init(a: Int64, b: Int64, c: Int64) { self.a = a; self.b = b; self.c = c }
}
