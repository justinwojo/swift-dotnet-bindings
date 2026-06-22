// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Frozen-struct Optional inline-layout ABI gate (SwiftValueLayout width vs decline)
//
// A `@frozen` struct's inline ABI layout is decided field-by-field. Two shapes exercise the two
// opposite outcomes of the value-layout oracle that sizes Optional fields:
//
//  * WIDTH PATH — a scalar Optional whose payload has NO spare bits (Int32?, Float?) ADDS a
//    discriminator tag byte after the payload. The oracle must size that appended tag correctly so the
//    field (and the whole struct) keeps a non-nil inline layout and is passed/returned by value in
//    registers. A struct of ONLY such fields rides the native-thunk register/width path end to end, so
//    a wrong width silently corrupts the round-tripped value. This is the real width fence.
//
//  * DECLINE PATH — Optional<Bool> reuses Bool's spare bit (no appended tag), which the oracle declines
//    to size inline. A single declining field forces the WHOLE struct's field layout to nil, routing it
//    to the indirect `@_cdecl` (whole-struct-by-pointer) path. This shape proves the decline routes to
//    a correct-but-indirect round-trip rather than fabricating a bad inline width.
//
// The split is load-bearing: a struct mixing a width field with a declining field would mask the width
// math, because the declining field alone forces the whole struct indirect and the width path is never
// exercised. Keep them in separate structs.

// MARK: Width path

/// `@frozen` struct whose stored fields are ONLY tag-adding scalar Optionals (no spare bits in either
/// payload), so the struct keeps a non-nil inline field layout and rides the by-value register/width
/// path. The round-trip fences the oracle's appended-tag width math: a wrong width corrupts a field.
@frozen
public struct FrozenScalarOptionalPair {
    public var first: Int32?
    public var second: Float?

    public init(first: Int32?, second: Float?) {
        self.first = first
        self.second = second
    }
}

/// By-value round-trip (param in, same value out) for the width-path struct. The native thunk carries
/// the struct through registers in both directions, so a corrupted inline width surfaces as a changed
/// field value or a crash.
public func roundTripFrozenScalarOptionalPair(_ v: FrozenScalarOptionalPair) -> FrozenScalarOptionalPair {
    return v
}

// MARK: Decline path

/// `@frozen` struct carrying an `Optional<Bool>` (spare-bit) field, which the value-layout oracle
/// declines to size inline; the declining field forces the whole struct to the indirect `@_cdecl`
/// (whole-struct-by-pointer) path. The accompanying `marker` rides the indirect buffer and confirms no
/// corruption. This exercises DECLINE -> `@_cdecl`, not the inline width math.
@frozen
public struct FrozenOptionalBoolHolder {
    public var flag: Bool?
    public var marker: Int32

    public init(flag: Bool?, marker: Int32) {
        self.flag = flag
        self.marker = marker
    }
}

/// By-value round-trip for the decline-path struct: passes the whole struct by pointer in and out.
public func roundTripFrozenOptionalBoolHolder(_ v: FrozenOptionalBoolHolder) -> FrozenOptionalBoolHolder {
    return v
}
