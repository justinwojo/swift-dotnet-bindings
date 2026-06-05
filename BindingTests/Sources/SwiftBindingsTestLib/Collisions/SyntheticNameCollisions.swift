// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Synthetic Wrapper Parameter Name Collisions (P1-22 class)
//
// The generator injects synthetic parameter bindings into the @_cdecl/@_silgen_name
// wrapper functions it emits — e.g. the instance-self pointer (`self_`), the indirect
// result buffer (`resultPtr`/`__resultPtr`), the throwing error out-param (`errorOut`),
// the simple-enum discriminator (`tag`), the setter value (`newValue`), and the
// default-parameter result buffer (`_resultBuf`). When a *user* parameter is spelled
// exactly like one of these synthetics, the generated Swift wrapper would declare two
// parameters with the same internal binding name, which `swiftc` rejects — and because
// the wrapper is compiled separately, the failure is SILENT: the binding compiles, the
// entry point is missing from the dylib, and the call crashes at runtime.
//
// The fix escapes the colliding USER binding (source-local rename; the external Swift
// call label is computed separately, so forwarding is unchanged). These fixtures pin one
// collision per emission path and round-trip the value to prove the wrapper survives and
// forwards correctly.

/// A frozen struct large enough to be returned indirectly (via the `resultPtr` buffer),
/// so a `resultPtr` user parameter collides with the result-buffer synthetic.
public struct WideResult {
    public let a: Int32
    public let b: Int32
    public let c: Int32
    public let d: Int32
    public let e: Int32
    public init(a: Int32, b: Int32, c: Int32, d: Int32, e: Int32) {
        self.a = a; self.b = b; self.c = c; self.d = d; self.e = e
    }
}

public enum SyntheticCollisionError: Error { case negative }

/// Instance methods whose user parameters are spelled like the synthetics injected into
/// the @_cdecl wrappers (`self_`, `resultPtr`, `errorOut`, `newValue`).
public class SyntheticParamCollider {
    private let base: Int32
    public init(base: Int32) { self.base = base }

    /// `self_` collides with the injected instance-self pointer on the @_cdecl wrapper.
    public func addSelf(self_: Int32) -> Int32 { return base + self_ }

    /// `resultPtr` collides with the indirect-result buffer synthetic (wide frozen return).
    public func makeWide(resultPtr: Int32) -> WideResult {
        return WideResult(a: resultPtr, b: resultPtr + 1, c: resultPtr + 2,
                          d: resultPtr + 3, e: resultPtr + 4)
    }

    /// `errorOut` collides with the throwing error out-param synthetic.
    public func mightFail(errorOut: Int32) throws -> Int32 {
        if errorOut < 0 { throw SyntheticCollisionError.negative }
        return errorOut * 2
    }

    /// `newValue` collides with the setter-value synthetic (used here on a plain method).
    public func bump(newValue: Int32) -> Int32 { return base + newValue }

    /// `self_` collides with the injected self pointer on the async @_cdecl wrapper.
    public func addSelfAsync(self_: Int32) async -> Int32 { return base + self_ }

    /// CONTROL (not an A repro): a plain blittable instance method takes the raw assembly
    /// register-shift thunk path (`thunk_<Module>_<hex>`), NOT a Swift @_cdecl wrapper — it
    /// has no Swift parameter bindings, so the sibling rename never runs here. Kept to pin
    /// that the thunk path forwards two params (one literally named `__tag`) correctly, and
    /// to document the architecture split: the A collision can only manifest on the @_cdecl
    /// wrapper path exercised by `tagPairWide` / `tagPairThrowing` below.
    public func tagPair(tag: Int32, __tag: Int32) -> Int32 { return tag * 1000 + __tag }

    /// A REPRO (wide frozen return forces a Swift @_cdecl wrapper). The user parameter `tag`
    /// (a reserved synthetic) is escaped to `__tag` by the wrapper's internal-binding rename.
    /// If the rename is not aware of the SIBLING user parameter literally named `__tag`, the
    /// two Swift bindings collide — `swiftc` rejects the wrapper and it is SILENTLY dropped
    /// from the separately-compiled dylib, leaving a missing entry point that crashes at call
    /// time. The sibling-aware fix renames `tag`→`__tag2` while `__tag` stays `__tag`. Encodes
    /// both inputs at distinct positions/scales so a swapped or dropped forward is caught:
    /// only correct positional forwarding yields a==tag*1000+__tag, b==tag, c==__tag.
    public func tagPairWide(tag: Int32, __tag: Int32) -> WideResult {
        return WideResult(a: tag * 1000 + __tag, b: tag, c: __tag,
                          d: tag - __tag, e: tag + __tag)
    }

    /// A REPRO (throwing forces a Swift @_cdecl wrapper with an injected `errorOut`). Same
    /// `tag`→`__tag` reserved-escape colliding with the sibling `__tag`; distinct path from
    /// the wide-return variant. Round-trips `tag*1000 + __tag` on the success branch.
    public func tagPairThrowing(tag: Int32, __tag: Int32) throws -> Int32 {
        if tag < 0 { throw SyntheticCollisionError.negative }
        return tag * 1000 + __tag
    }
}

/// Initializer whose parameters are spelled like the constructor wrapper synthetics
/// (`resultPtr`, `self_`).
public class SyntheticInitCollider {
    public let total: Int32
    public init(resultPtr: Int32, self_: Int32) { self.total = resultPtr + self_ }
}

/// Simple (Int32 raw value) enum with methods whose parameter is named `tag` — the
/// discriminator synthetic the simple-enum @_cdecl wrapper injects.
public enum Knob: Int32 {
    case off = 0
    case on = 1

    public func combine(tag: Int32) -> Int32 { return rawValue + tag }
    public static func fromTag(tag: Int32) -> Knob { return tag == 0 ? .off : .on }
}

/// Default-parameter method whose parameter is named `_resultBuf` — the default-overload
/// result-buffer synthetic.
public class SyntheticDefaultCollider {
    public init() {}
    public func go(_resultBuf: Int32 = 5, extra: Int32 = 10) -> Int32 { return _resultBuf + extra }
}
