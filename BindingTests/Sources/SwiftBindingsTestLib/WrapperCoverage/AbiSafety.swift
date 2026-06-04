// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Frozen Struct with Float Fields (RequiresCdeclForAbiSafety: float)

/// Frozen struct with 4 Double fields — exercises IsSelfTypeCdeclRequired
/// float field detection (WrapperValidation.cs line 877).
/// Real-world pattern: Lottie LottieColor (r/g/b/a: Double).
/// Methods on this struct MUST go through @_cdecl to avoid Mono JIT crash.
@frozen
public struct LottieColorLike {
    public var r: Double
    public var g: Double
    public var b: Double
    public var a: Double

    public init(r: Double, g: Double, b: Double, a: Double) {
        self.r = r
        self.g = g
        self.b = b
        self.a = a
    }

    /// Instance method — must use @_cdecl wrapper.
    public func brightness() -> Double {
        return (r + g + b) / 3.0
    }

    /// Another instance method to verify all methods route through @_cdecl.
    public func withAlpha(_ newAlpha: Double) -> LottieColorLike {
        return LottieColorLike(r: r, g: g, b: b, a: newAlpha)
    }

    /// Returns a descriptive string.
    public func describe() -> String {
        return "RGBA(\(r), \(g), \(b), \(a))"
    }
}

// MARK: - Frozen Struct with Bool Fields (RequiresCdeclForAbiSafety: bool)

/// Frozen struct with Bool fields — exercises IsSelfTypeCdeclRequired
/// bool field detection (WrapperValidation.cs line 881).
/// Bool fields in Swift use i1 which Mono JIT can't pass via CallConvSwift registers.
@frozen
public struct FeatureFlags {
    public var enableLogging: Bool
    public var enableCache: Bool
    public var debugMode: Bool

    public init(enableLogging: Bool, enableCache: Bool, debugMode: Bool) {
        self.enableLogging = enableLogging
        self.enableCache = enableCache
        self.debugMode = debugMode
    }

    /// Instance method — must use @_cdecl wrapper.
    public func activeCount() -> Int32 {
        var count: Int32 = 0
        if enableLogging { count += 1 }
        if enableCache { count += 1 }
        if debugMode { count += 1 }
        return count
    }

    /// Returns combined flag state.
    public func allEnabled() -> Bool {
        return enableLogging && enableCache && debugMode
    }

    public func describe() -> String {
        return "Flags(log:\(enableLogging), cache:\(enableCache), debug:\(debugMode))"
    }
}

// MARK: - Frozen Struct >8 Bytes (RequiresCdeclForAbiSafety: size)

/// Frozen struct with 3 Int fields (24 bytes) — exercises IsSelfTypeCdeclRequired
/// size > 8 bytes detection (WrapperValidation.cs line 888).
/// Structs larger than 8 bytes are passed indirectly and need @_cdecl.
@frozen
public struct LargeConfig {
    public var width: Int
    public var height: Int
    public var depth: Int

    public init(width: Int, height: Int, depth: Int) {
        self.width = width
        self.height = height
        self.depth = depth
    }

    /// Instance method — must use @_cdecl wrapper.
    public func volume() -> Int {
        return width * height * depth
    }

    /// Another instance method.
    public func surfaceArea() -> Int {
        return 2 * (width * height + height * depth + width * depth)
    }

    public func describe() -> String {
        return "\(width)x\(height)x\(depth)"
    }
}

// MARK: - Class with Non-Blittable Constructor (BUG-3 coverage)

/// Class with both a simple constructor and one with Array<String> parameter.
/// Array<T> is a generic container that requires @_cdecl wrapper because it's
/// non-blittable in CallConvSwift. Without the wrapper, Mono JIT crashes.
/// Real-world pattern: Kingfisher ImagePrefetcher(urls:options:completionHandler:).
///
/// BUG-3 fix: When no wrapper strategy is available (e.g., third-party xcframework),
/// the generator now suppresses the constructor instead of emitting a raw
/// CallConvSwift P/Invoke that crashes. In BindingTests (with wrapper support),
/// the @_cdecl wrapper handles it correctly.
public class ArrayInitHolder {
    public var count: Int32
    public var label: String

    /// Simple constructor — always works (blittable params).
    public init(count: Int32) {
        self.count = count
        self.label = "count-only"
    }

    /// Constructor with Array<String> — non-blittable, requires @_cdecl wrapper.
    /// In BindingTests, the wrapper is generated. In third-party libs without
    /// wrapper support, BUG-3 fix suppresses this to prevent Mono JIT crash.
    public init(items: [String]) {
        self.count = Int32(items.count)
        self.label = items.joined(separator: ", ")
    }

    /// Instance method for verification.
    public func describe() -> String {
        return "ArrayInitHolder(count: \(count), label: \(label))"
    }
}

// MARK: - Non-Frozen Struct with Instance Methods (RequiresCdeclForAbiSafety: non-frozen)

/// Non-frozen struct with instance methods — exercises
/// RequiresCdeclForAbiSafety:713 (IsNonFrozenStructInstanceMember).
/// Non-frozen structs always need @_cdecl because their layout is opaque.
public struct FlexibleConfig {
    public var name: String
    public var retryCount: Int32

    public init(name: String, retryCount: Int32) {
        self.name = name
        self.retryCount = retryCount
    }

    /// Instance method — must use @_cdecl because non-frozen.
    public func shouldRetry() -> Bool {
        return retryCount > 0
    }

    /// Another instance method.
    public func describe() -> String {
        return "\(name): retries=\(retryCount)"
    }
}

// MARK: - Register-Spill Free Function (x86_64 thunk symmetry → @_cdecl fallback)

/// Free function taking SEVEN Int parameters. On arm64 the eight AAPCS64 integer argument registers
/// (x0–x7) hold all seven, so a native thunk can bridge the call; on x86_64 SysV there are only six
/// integer argument registers (rdi, rsi, rdx, rcx, r8, r9), so the seventh would spill to the stack
/// and the x86_64 thunk declines. Because the generated C# imports a single architecture-neutral
/// thunk symbol, the generator must NOT emit an arm64-only thunk here — it falls the whole method
/// back to the @_cdecl wrapper, which is correct on both architectures. The value round-trip proves
/// the wrapper path resolves (no missing-symbol EntryPointNotFound on the x86_64 slice under Rosetta).
public func sumSevenInts(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ e: Int, _ f: Int, _ g: Int) -> Int {
    return a + b + c + d + e + f + g
}

// MARK: - sret + SwiftSelf combination probe (direct CallConvSwift register shape)

/// Frozen struct whose MUTATING method returns another large value INDIRECTLY (SwiftIndirectResult /
/// sret) while taking `self` by `inout` (a pointer in the swiftcc self register) plus two explicit
/// integer arguments. This is the minimal NON-GENERIC reproduction of the exact register shape used
/// by `Swift.stdlib`'s `Dictionary.updateValue(_:forKey:)` — sret in the indirect-result register,
/// integer arguments in the first GPRs, and a self pointer in the self register — combining
/// SwiftIndirectResult AND SwiftSelf in one direct `CallConvSwift` call. `SretSelfProbeTests`
/// hand-marshals a raw `CallConvSwift` P/Invoke against this symbol (no stdlib generics, metadata, or
/// value-witness tables involved) to isolate the calling-convention trampoline from every higher
/// layer. The `@_cdecl` control below performs the identical computation across a plain C ABI.
///
/// FIVE Int fields (40 bytes) are required: under x86_64 swiftcc an all-integer aggregate of up to
/// four eightbytes is returned DIRECTLY in registers (rax/rdx/rcx/r8), so a smaller struct would not
/// exercise the indirect-result register at all. At 40 bytes the return is classified indirect — the
/// `combine` body writes its result through the sret pointer in %rax while `self` arrives in %r13 and
/// `x`/`y` in %rdi/%rsi (verified by disassembly).
@frozen
public struct SretSelfProbe {
    public var a: Int
    public var b: Int
    public var c: Int
    public var d: Int
    public var e: Int

    public init(a: Int, b: Int, c: Int, d: Int, e: Int) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.e = e
    }

    /// inout self (pointer in the self register) + two integer args + 40-byte indirect (sret) return.
    public mutating func combine(_ x: Int, _ y: Int) -> SretSelfProbe {
        a &+= x
        b &+= y
        c &+= x &+ y
        d &+= x
        e &+= y
        return SretSelfProbe(a: a, b: b, c: c, d: d, e: e)
    }
}

/// Plain C ABI control for `SretSelfProbe.combine`: self via an `inout` pointer, the two integer args
/// by value, and the result written through an out pointer — none of which travels in the swiftcc
/// indirect-result or self registers at the managed boundary. If the direct `CallConvSwift` probe
/// crashes while this passes on the same target, the fault is the calling-convention trampoline, not
/// the Swift method or the marshalling.
@_cdecl("sbw_sretselfprobe_combine_cdecl")
public func sbw_sretselfprobe_combine_cdecl(
    _ selfPtr: UnsafeMutableRawPointer,
    _ x: Int,
    _ y: Int,
    _ outResult: UnsafeMutableRawPointer
) {
    let selfBound = selfPtr.assumingMemoryBound(to: SretSelfProbe.self)
    let result = selfBound.pointee.combine(x, y)
    outResult.assumingMemoryBound(to: SretSelfProbe.self).initialize(to: result)
}

// MARK: - P1-15: frozen struct with an UNSIZEABLE generic value-type stored field (fail-closed skip)

/// `@frozen` struct carrying a `ClosedRange<Int>?` stored field. ClosedRange<Bound> is a frozen,
/// reference-managed value type whose inline size depends on its Bound argument
/// (`MemoryLayout<ClosedRange<Int>>` = 16 but `<ClosedRange<Float>>` = 8). The bare TypeDatabase
/// record strips the generic arguments, so there is no persisted InlineSize, and the iOS/device
/// slice exposes no live metadata — the per-instantiation Buffer size cannot be derived
/// cross-compile. The generator therefore FAILS CLOSED and skips `RangeHolder`
/// (SkipReason.IndeterminateStructLayout) rather than emit a guessed `Buffer` layout that would
/// mis-size the field and corrupt the heap. Because the type is skipped, every free function that
/// passes or returns it by value (e.g. `describeRangeHolder`) must be pruned in the same pass; the
/// `--compile-only` gate verifies the prune is clean (no dangling reference to a non-emitted
/// `RangeHolder.Buffer`).
@frozen
public struct RangeHolder {
    public var bounds: ClosedRange<Int>?
    public var marker: Int

    public init(marker: Int) {
        self.bounds = nil
        self.marker = marker
    }
}

/// References `RangeHolder` by value. After the fail-closed skip this whole function must be pruned
/// from the generated bindings (its signature reaches a skipped type), proving the skip propagates
/// to dependent members instead of leaving a dangling `RangeHolder.Buffer` reference behind.
public func describeRangeHolder(_ h: RangeHolder) -> Int {
    return h.marker
}

// MARK: - P1-15: frozen struct whose multi-word reference field MUST size correctly (persist path)

/// `@frozen` struct whose first stored field is an `AnyHashable?` (a non-generic reference-managed
/// type with a FIXED 40-byte existential box, persisted as `inlineSize="40"` in SwiftDatabase.xml),
/// followed by a plain `Int` tag. `Optional<AnyHashable>` is also 40 bytes (AnyHashable has extra
/// inhabitants, so the optional reuses a spare bit pattern), placing `tag` at byte offset 40. The
/// historical bug clamped any reference-managed field with no persisted size to a single 8-byte
/// pointer, which would lay `tag` at offset 8 in the C# Buffer — reading garbage and corrupting the
/// heap on round-trip. With the size persisted, the Buffer reserves the correct 40 bytes for `key`
/// and `tag` round-trips intact. `key` is deliberately left `nil` so no ARC box is involved and the
/// test isolates the field-offset/Buffer-size behaviour from existential boxing.
@frozen
public struct HashHolder {
    public var key: AnyHashable?
    public var tag: Int

    public init(tag: Int) {
        self.key = nil
        self.tag = tag
    }

    public func readTag() -> Int {
        return tag
    }
}

/// Constructs a `HashHolder` in Swift (key = nil, given tag) and returns it by value — the returned
/// 48-byte buffer must be copied into the C# `Buffer` with `tag` at offset 40.
public func makeHashHolder(tag: Int) -> HashHolder {
    return HashHolder(tag: tag)
}

/// Round-trips a `HashHolder` by value back into Swift and reads its `tag`. If the C# Buffer
/// mis-sized `key` (the old single-pointer clamp), `tag` lands at the wrong offset and this returns
/// the wrong value (or corrupts the heap); with the 40-byte size persisted it returns `tag` intact.
public func hashHolderRoundTripTag(_ h: HashHolder) -> Int {
    return h.tag
}

// MARK: - P1-15: frozen-as-class struct whose Optional<8-byte-primitive> field MUST size to two words

/// `@frozen` struct combining a reference-managed `AnyHashable?` first field (forces the
/// `ClassWithBufferStruct` projection — a C# class with a nested blitted `Buffer`, exactly as
/// `HashHolder`) with an `Optional<Int>` MIDDLE field and a trailing `Int` tag. `Optional<Int>` is a
/// fixed-width primitive optional with NO extra inhabitants (Int uses its full bit range), so it
/// carries a separate tag byte: `MemoryLayout<Int?>.size == 9`, occupying two 8-byte words. The
/// historical bug resolved every `Optional<primitive>` to a single pointer (the reference-field
/// fallback clamp), laying the Buffer's `maybeValue` slot at one word (8 bytes) instead of two — which
/// shifts `tag` from its true offset (56) down to 48 and under-sizes the whole Buffer by 8 bytes, so
/// `tag` reads garbage and the round-trip copy overruns. With `Optional<Int>` sized to two words the
/// Buffer matches Swift's 64-byte layout and `tag` round-trips intact. `key` is left `nil` so no ARC
/// box is involved and the test isolates the Optional-primitive field sizing. (Verified offsets:
/// key=0, maybeValue=40, tag=56, size=64.) `Optional<Int>` as a non-last field also proves the
/// two-word slot does not collapse against a following scalar — the precise case `HashHolder`
/// (multi-word reference field) does not cover.
@frozen
public struct PrimitiveOptionalHolder {
    public var key: AnyHashable?
    public var maybeValue: Int?
    public var tag: Int

    public init(maybeValue: Int?, tag: Int) {
        self.key = nil
        self.maybeValue = maybeValue
        self.tag = tag
    }

    public func readTag() -> Int {
        return tag
    }
}

/// Constructs a `PrimitiveOptionalHolder` in Swift (key = nil, given values) and returns it by value —
/// the returned 64-byte buffer must be copied into the C# `Buffer` with `maybeValue` occupying two
/// words and `tag` at offset 56.
public func makePrimitiveOptionalHolder(maybeValue: Int, tag: Int) -> PrimitiveOptionalHolder {
    return PrimitiveOptionalHolder(maybeValue: maybeValue, tag: tag)
}

/// Constructs a `PrimitiveOptionalHolder` whose `maybeValue` is `nil` — the Optional's nil tag byte
/// must sit at the correct in-word offset for `tag` (offset 56) to still read back intact. Built
/// Swift-side so the test does not depend on marshalling a `nil` `Optional<Int>` constructor argument.
public func makePrimitiveOptionalHolderNil(tag: Int) -> PrimitiveOptionalHolder {
    return PrimitiveOptionalHolder(maybeValue: nil, tag: tag)
}

/// Round-trips a `PrimitiveOptionalHolder` by value back into Swift and returns `tag &+ (maybeValue ??
/// 0)`. If the C# Buffer mis-sized `maybeValue` to one word (the old clamp), `tag` lands at the wrong
/// offset and this returns the wrong value (or corrupts the heap); with the two-word size it returns
/// the expected sum intact. Handles both the some(value) and nil cases.
public func primitiveOptionalHolderRoundTrip(_ h: PrimitiveOptionalHolder) -> Int {
    return h.tag &+ (h.maybeValue ?? 0)
}
