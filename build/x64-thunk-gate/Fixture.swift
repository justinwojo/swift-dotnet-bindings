// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Fixture library for the x86_64 (SysV) thunk-backend gate.
//
// Every member here exercises a distinct corner of the swiftcc -> cdecl bridge
// that the x86_64 thunk backend must get right:
//   - instance method        -> self in %r13
//   - throwing instance method-> error in %r12 (swifterror), out-param writeback
//   - static method           -> metatype accessor call before the swiftcc call
//   - >16B mixed int/float return-by-value -> field-wise return-store bridge
//
// `Mixed` is deliberately `@frozen`: only frozen 17-32B structs returned by value
// get a register-return-bridge thunk. A resilient (non-@frozen, library-evolution)
// struct becomes an opaque-payload class returned indirectly via the @_cdecl
// wrapper and never reaches the return-store path this gate is built to prove.

import Foundation

// 24-byte mixed-width struct: Int32@0, Float@4, Int64@8, Double@16.
// >16B so cdecl returns it via an sret buffer while swiftcc returns it field-wise
// across rax/xmm0/rdx/xmm1 — the thunk must scatter those registers into the buffer.
@frozen public struct Mixed {
    public var i: Int32
    public var f: Float
    public var j: Int64
    public var d: Double
    public init(i: Int32, f: Float, j: Int64, d: Double) {
        self.i = i; self.f = f; self.j = j; self.d = d
    }
}

public enum FixtureError: Error { case boom }

public final class Counter {
    private var value: Int64
    public init(start: Int64) { value = start }

    // instance method, self in swiftself (%r13)
    public func addAndGet(_ delta: Int64) -> Int64 { value &+= delta; return value }

    // instance method returning a >16B mixed-width struct (return bridge)
    public func snapshot(scale: Double) -> Mixed {
        return Mixed(i: Int32(value), f: Float(value) * 2.0, j: value, d: Double(value) * scale)
    }

    // throwing instance method (error in %r12 / swifterror)
    public func checkedAdd(_ delta: Int64) throws -> Int64 {
        if delta < 0 { throw FixtureError.boom }
        value &+= delta
        return value
    }

    // static method (metatype accessor)
    public static func origin() -> Int64 { return 0 }

    // static method returning a mixed-width struct (metatype + return bridge)
    public static func makeMixed(_ base: Int64) -> Mixed {
        return Mixed(i: Int32(base), f: Float(base) + 0.5, j: base &* 2, d: Double(base) * 1.5)
    }
}
