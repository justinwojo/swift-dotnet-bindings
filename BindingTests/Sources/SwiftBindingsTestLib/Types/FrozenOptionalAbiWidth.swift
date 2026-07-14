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

// MARK: Self-by-value path (Optional-float self must decline the direct SwiftSelf<T> stub)

/// SMALL (≤ 8 byte) `@frozen` struct with a SINGLE tag-adding scalar-Optional field — `Float?` is
/// 5 bytes ("f4,i1"), so it stays under the by-value self-size limit and has one stored property.
/// Neither the size nor the property-count guard fires, so the ONLY thing that can route an instance
/// method's `self` off the direct SwiftSelf<T> by-value stub is the struct's float-fields flag. A
/// by-name field classifier that never unwraps Optional leaves that flag clear and passes this struct
/// through the direct stub, where the Optional's float payload lands in a floating-point register in
/// Swift but the .NET CallConvSwift stub assigns it as integer — silent corruption. The primitive
/// instance methods below carry `self` by value, so a mis-assigned self register shows up as a wrong
/// result or a crash.
@frozen
public struct FrozenOptionalFloatSelf {
    public var value: Float?

    public init(value: Float?) {
        self.value = value
    }

    /// Primitive-signature instance method: reads the Optional-float `self`, returns a primitive.
    public func scaled(by factor: Float) -> Float {
        guard let v = value else { return -1 }
        return v * factor
    }

    /// Primitive-signature instance method with no parameters — the barest self-by-value shape.
    public func rawOrSentinel() -> Float {
        return value ?? -999
    }
}
