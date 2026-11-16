// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Frozen structs that mix Bool with other fields, passed and returned by value.
//
// Swift lowers each to the registers noted beside it, identically on arm64 and x86_64, while the C
// convention groups the same bytes into eightbytes, or into memory once the struct passes 16 bytes.
// No binding hands one of these to C by value: a native thunk would need its P/Invoke to carry the
// struct, which Mono's AOT compiler cannot build a wrapper for, so every member goes through a
// @_cdecl wrapper that moves the value through memory.
//
// An i1 in a register is defined in its low byte only. `raw & k != 0` lowers to a truncation of raw,
// so on x86_64 the Bool's register keeps raw's bits above that byte: a false with nonzero upper bits
// (`makePackedBoolPair(raw: 0x100)` returns flag in al = 0 with eax = 0x100). Every reader has to
// look at the low byte alone. The `make` functions build each Bool field i from bit i of raw and
// every other field from raw >> 8, so the tests choose raw to put garbage where the ABI allows it.

// (i1, float): one INTEGER eightbyte to C
@frozen public struct PackedBoolFloat {
    public var a: Bool
    public var f: Float
    public init(a: Bool, f: Float) { self.a = a; self.f = f }
}
public func makePackedBoolFloat(raw: Int) -> PackedBoolFloat { PackedBoolFloat(a: raw & 1 != 0, f: Float(raw >> 8)) }
public func echoPackedBoolFloat(_ p: PackedBoolFloat) -> PackedBoolFloat { p }

// (float, i1): one INTEGER eightbyte to C
@frozen public struct PackedFloatBool {
    public var f: Float
    public var a: Bool
    public init(f: Float, a: Bool) { self.f = f; self.a = a }
}
public func makePackedFloatBool(raw: Int) -> PackedFloatBool { PackedFloatBool(f: Float(raw >> 8), a: raw & 2 != 0) }
public func echoPackedFloatBool(_ p: PackedFloatBool) -> PackedFloatBool { p }

// (i1, float, i1): two INTEGER eightbytes to C
@frozen public struct PackedBoolFloatBool {
    public var a: Bool
    public var f: Float
    public var c: Bool
    public init(a: Bool, f: Float, c: Bool) { self.a = a; self.f = f; self.c = c }
}
public func makePackedBoolFloatBool(raw: Int) -> PackedBoolFloatBool { PackedBoolFloatBool(a: raw & 1 != 0, f: Float(raw >> 8), c: raw & 4 != 0) }
public func echoPackedBoolFloatBool(_ p: PackedBoolFloatBool) -> PackedBoolFloatBool { p }

// i64: both Bools share the word with the Int32
@frozen public struct PackedBoolBoolInt32 {
    public var a: Bool
    public var b: Bool
    public var n: Int32
    public init(a: Bool, b: Bool, n: Int32) { self.a = a; self.b = b; self.n = n }
}
public func makePackedBoolBoolInt32(raw: Int) -> PackedBoolBoolInt32 { PackedBoolBoolInt32(a: raw & 1 != 0, b: raw & 2 != 0, n: Int32(truncatingIfNeeded: raw >> 8)) }
public func echoPackedBoolBoolInt32(_ p: PackedBoolBoolInt32) -> PackedBoolBoolInt32 { p }

// i64: the Bool sits in byte 4 of the word
@frozen public struct PackedInt32Bool {
    public var n: Int32
    public var a: Bool
    public init(n: Int32, a: Bool) { self.n = n; self.a = a }
}
public func makePackedInt32Bool(raw: Int) -> PackedInt32Bool { PackedInt32Bool(n: Int32(truncatingIfNeeded: raw >> 8), a: raw & 2 != 0) }
public func echoPackedInt32Bool(_ p: PackedInt32Bool) -> PackedInt32Bool { p }

// (i64, i1): the first Bool shares a word, the second has its own
@frozen public struct PackedBoolInt32Bool {
    public var a: Bool
    public var b: Int32
    public var c: Bool
    public init(a: Bool, b: Int32, c: Bool) { self.a = a; self.b = b; self.c = c }
}
public func makePackedBoolInt32Bool(raw: Int) -> PackedBoolInt32Bool { PackedBoolInt32Bool(a: raw & 1 != 0, b: Int32(truncatingIfNeeded: raw >> 8), c: raw & 4 != 0) }
public func echoPackedBoolInt32Bool(_ p: PackedBoolInt32Bool) -> PackedBoolInt32Bool { p }

// (i1, double, i1): in memory to C, three registers to Swift
@frozen public struct PackedBoolDoubleBool {
    public var a: Bool
    public var d: Double
    public var b: Bool
    public init(a: Bool, d: Double, b: Bool) { self.a = a; self.d = d; self.b = b }
}
public func makePackedBoolDoubleBool(raw: Int) -> PackedBoolDoubleBool { PackedBoolDoubleBool(a: raw & 1 != 0, d: Double(raw >> 8), b: raw & 4 != 0) }
public func echoPackedBoolDoubleBool(_ p: PackedBoolDoubleBool) -> PackedBoolDoubleBool { p }

// (i64, i1, i64): in memory to C, three registers to Swift
@frozen public struct PackedIntBoolInt {
    public var n: Int
    public var a: Bool
    public var m: Int
    public init(n: Int, a: Bool, m: Int) { self.n = n; self.a = a; self.m = m }
}
public func makePackedIntBoolInt(raw: Int) -> PackedIntBoolInt { PackedIntBoolInt(n: raw >> 8, a: raw & 2 != 0, m: raw >> 8) }
public func echoPackedIntBoolInt(_ p: PackedIntBoolInt) -> PackedIntBoolInt { p }

// (i1, i64)
@frozen public struct PackedBoolPair {
    public var flag: Bool
    public var n: Int
    public init(flag: Bool, n: Int) { self.flag = flag; self.n = n }
}
public func makePackedBoolPair(raw: Int) -> PackedBoolPair { PackedBoolPair(flag: raw & 1 != 0, n: raw >> 8) }
public func echoPackedBoolPair(_ p: PackedBoolPair) -> PackedBoolPair { p }

// i32: two Bools and a UInt16 in one word
@frozen public struct PackedBoolQuad {
    public var x: Bool
    public var y: Bool
    public var z: UInt16
    public init(x: Bool, y: Bool, z: UInt16) { self.x = x; self.y = y; self.z = z }
}
public func makePackedBoolQuad(raw: Int) -> PackedBoolQuad { PackedBoolQuad(x: raw & 1 != 0, y: raw & 2 != 0, z: UInt16(truncatingIfNeeded: raw >> 8)) }
public func echoPackedBoolQuad(_ p: PackedBoolQuad) -> PackedBoolQuad { p }

// The same shapes through a class's members, which reach Swift through @_cdecl wrappers as well.
public final class PackedBoolHost {
    public init() {}
    public func makePackedBoolFloat(raw: Int) -> PackedBoolFloat { SwiftBindingsTestLib.makePackedBoolFloat(raw: raw) }
    public func echoPackedBoolFloat(_ p: PackedBoolFloat) -> PackedBoolFloat { p }
    public func makePackedFloatBool(raw: Int) -> PackedFloatBool { SwiftBindingsTestLib.makePackedFloatBool(raw: raw) }
    public func echoPackedFloatBool(_ p: PackedFloatBool) -> PackedFloatBool { p }
    public func makePackedBoolFloatBool(raw: Int) -> PackedBoolFloatBool { SwiftBindingsTestLib.makePackedBoolFloatBool(raw: raw) }
    public func echoPackedBoolFloatBool(_ p: PackedBoolFloatBool) -> PackedBoolFloatBool { p }
    public func makePackedBoolBoolInt32(raw: Int) -> PackedBoolBoolInt32 { SwiftBindingsTestLib.makePackedBoolBoolInt32(raw: raw) }
    public func echoPackedBoolBoolInt32(_ p: PackedBoolBoolInt32) -> PackedBoolBoolInt32 { p }
    public func makePackedInt32Bool(raw: Int) -> PackedInt32Bool { SwiftBindingsTestLib.makePackedInt32Bool(raw: raw) }
    public func echoPackedInt32Bool(_ p: PackedInt32Bool) -> PackedInt32Bool { p }
    public func makePackedBoolInt32Bool(raw: Int) -> PackedBoolInt32Bool { SwiftBindingsTestLib.makePackedBoolInt32Bool(raw: raw) }
    public func echoPackedBoolInt32Bool(_ p: PackedBoolInt32Bool) -> PackedBoolInt32Bool { p }
    public func makePackedBoolDoubleBool(raw: Int) -> PackedBoolDoubleBool { SwiftBindingsTestLib.makePackedBoolDoubleBool(raw: raw) }
    public func echoPackedBoolDoubleBool(_ p: PackedBoolDoubleBool) -> PackedBoolDoubleBool { p }
    public func makePackedIntBoolInt(raw: Int) -> PackedIntBoolInt { SwiftBindingsTestLib.makePackedIntBoolInt(raw: raw) }
    public func echoPackedIntBoolInt(_ p: PackedIntBoolInt) -> PackedIntBoolInt { p }
    public func makePackedBoolPair(raw: Int) -> PackedBoolPair { SwiftBindingsTestLib.makePackedBoolPair(raw: raw) }
    public func echoPackedBoolPair(_ p: PackedBoolPair) -> PackedBoolPair { p }
    public func makePackedBoolQuad(raw: Int) -> PackedBoolQuad { SwiftBindingsTestLib.makePackedBoolQuad(raw: raw) }
    public func echoPackedBoolQuad(_ p: PackedBoolQuad) -> PackedBoolQuad { p }
    public static func makePair(raw: Int) -> PackedBoolPair { SwiftBindingsTestLib.makePackedBoolPair(raw: raw) }
    public func lowBit(raw: Int) -> Bool { raw & 1 != 0 }
    public var stored: PackedBoolQuad = PackedBoolQuad(x: false, y: true, z: 7)
    public func later(raw: Int) async -> PackedBoolInt32Bool { SwiftBindingsTestLib.makePackedBoolInt32Bool(raw: raw) }
}

// The remaining routes, on the shapes with a Bool alone in its register.
extension PackedBoolInt32Bool {
    public func flipped() -> PackedBoolInt32Bool { PackedBoolInt32Bool(a: !a, b: b, c: !c) }
    public var summary: Int { (a ? 1 : 0) + (c ? 2 : 0) + Int(b) * 4 }
}
public func packedBoolLowBit(raw: Int) -> Bool { raw & 1 != 0 }
public func packedBoolPairFlag(_ p: PackedBoolPair) -> Bool { p.flag }
public func packedBoolThroughCallback(raw: Int, _ f: (PackedBoolPair) -> PackedBoolPair) -> PackedBoolPair {
    f(makePackedBoolPair(raw: raw))
}
public struct PackedBoolGenericBox<T> {
    public var item: T
    public init(item: T) { self.item = item }
    public func make(raw: Int) -> PackedBoolInt32Bool { makePackedBoolInt32Bool(raw: raw) }
    public func echo(_ p: PackedBoolInt32Bool) -> PackedBoolInt32Bool { p }
}
public protocol PackedBoolSource { func pair(raw: Int) -> PackedBoolPair }
public func packedBoolFromSource(_ s: PackedBoolSource, raw: Int) -> PackedBoolPair { s.pair(raw: raw) }
