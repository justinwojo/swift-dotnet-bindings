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
