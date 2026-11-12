// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The P0 encoding grammar: frozen structs of every scalar mix and argument lists that cross the
// register limits, generated from the encoding probe's case table. The lowering oracle compiles
// this module and checks the predicted physical signature of every function against swiftc's IR.

import Foundation

@frozen
public struct P0I1 {
    public var f0: Int64
    public init(f0: Int64) { self.f0 = f0 }
}

@frozen
public struct P0I2 {
    public var f0: Int64
    public var f1: Int64
    public init(f0: Int64, f1: Int64) { self.f0 = f0; self.f1 = f1 }
}

@frozen
public struct P0I3 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public init(f0: Int64, f1: Int64, f2: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2 }
}

@frozen
public struct P0I4 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public var f3: Int64
    public init(f0: Int64, f1: Int64, f2: Int64, f3: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3 }
}

@frozen
public struct P0I5 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public var f3: Int64
    public var f4: Int64
    public init(f0: Int64, f1: Int64, f2: Int64, f3: Int64, f4: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3; self.f4 = f4 }
}

@frozen
public struct P0I6 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public var f3: Int64
    public var f4: Int64
    public var f5: Int64
    public init(f0: Int64, f1: Int64, f2: Int64, f3: Int64, f4: Int64, f5: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3; self.f4 = f4; self.f5 = f5 }
}

@frozen
public struct P0I7 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public var f3: Int64
    public var f4: Int64
    public var f5: Int64
    public var f6: Int64
    public init(f0: Int64, f1: Int64, f2: Int64, f3: Int64, f4: Int64, f5: Int64, f6: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3; self.f4 = f4; self.f5 = f5; self.f6 = f6 }
}

@frozen
public struct P0I8 {
    public var f0: Int64
    public var f1: Int64
    public var f2: Int64
    public var f3: Int64
    public var f4: Int64
    public var f5: Int64
    public var f6: Int64
    public var f7: Int64
    public init(f0: Int64, f1: Int64, f2: Int64, f3: Int64, f4: Int64, f5: Int64, f6: Int64, f7: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3; self.f4 = f4; self.f5 = f5; self.f6 = f6; self.f7 = f7 }
}

@frozen
public struct P0DI {
    public var f0: Double
    public var f1: Int64
    public init(f0: Double, f1: Int64) { self.f0 = f0; self.f1 = f1 }
}

@frozen
public struct P0FFI {
    public var f0: Float
    public var f1: Float
    public var f2: Int64
    public init(f0: Float, f1: Float, f2: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2 }
}

@frozen
public struct P0IFD {
    public var f0: Int32
    public var f1: Float
    public var f2: Double
    public init(f0: Int32, f1: Float, f2: Double) { self.f0 = f0; self.f1 = f1; self.f2 = f2 }
}

@frozen
public struct P0D4 {
    public var f0: Double
    public var f1: Double
    public var f2: Double
    public var f3: Double
    public init(f0: Double, f1: Double, f2: Double, f3: Double) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3 }
}

@frozen
public struct P0D5 {
    public var f0: Double
    public var f1: Double
    public var f2: Double
    public var f3: Double
    public var f4: Double
    public init(f0: Double, f1: Double, f2: Double, f3: Double, f4: Double) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3; self.f4 = f4 }
}

@frozen
public struct P0FF {
    public var f0: Float
    public var f1: Float
    public init(f0: Float, f1: Float) { self.f0 = f0; self.f1 = f1 }
}

@frozen
public struct P0FD {
    public var f0: Float
    public var f1: Double
    public init(f0: Float, f1: Double) { self.f0 = f0; self.f1 = f1 }
}

@frozen
public struct P0BHWQ {
    public var f0: Int8
    public var f1: Int16
    public var f2: Int32
    public var f3: Int64
    public init(f0: Int8, f1: Int16, f2: Int32, f3: Int64) { self.f0 = f0; self.f1 = f1; self.f2 = f2; self.f3 = f3 }
}

@frozen
public struct P0BBH {
    public var f0: UInt8
    public var f1: Int8
    public var f2: Int16
    public init(f0: UInt8, f1: Int8, f2: Int16) { self.f0 = f0; self.f1 = f1; self.f2 = f2 }
}

public func p0_c00() -> Int64 { 0 }
public func p0_c01(_ a0: Int64) -> Int64 { 0 }
public func p0_c02(_ a0: P0I1) -> Int64 { 0 }
public func p0_c03(_ a0: P0I2) -> Int64 { 0 }
public func p0_c04(_ a0: P0I3) -> Int64 { 0 }
public func p0_c05(_ a0: P0I4) -> Int64 { 0 }
public func p0_c06(_ a0: P0I5) -> Int64 { 0 }
public func p0_c07(_ a0: P0I6) -> Int64 { 0 }
public func p0_c08(_ a0: P0I7) -> Int64 { 0 }
public func p0_c09(_ a0: P0I8) -> Int64 { 0 }
public func p0_c10(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: P0I2) -> Int64 { 0 }
public func p0_c11(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: P0I2) -> Int64 { 0 }
public func p0_c12(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: P0I3) -> Int64 { 0 }
public func p0_c13(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64) -> Int64 { 0 }
public func p0_c14(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64) -> Int64 { 0 }
public func p0_c15(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64, _ a9: Int64, _ a10: Int64, _ a11: Int64) -> Int64 { 0 }
public func p0_c16(_ a0: Double, _ a1: Double, _ a2: Double, _ a3: Double, _ a4: Double, _ a5: Double, _ a6: Double, _ a7: Double) -> Double { 0 }
public func p0_c17(_ a0: Double, _ a1: Double, _ a2: Double, _ a3: Double, _ a4: Double, _ a5: Double, _ a6: Double, _ a7: Double, _ a8: Double, _ a9: Double) -> Double { 0 }
public func p0_c18(_ a0: Float, _ a1: Float, _ a2: Float, _ a3: Float, _ a4: Float, _ a5: Float, _ a6: Float, _ a7: Float, _ a8: Float, _ a9: Double, _ a10: Float) -> Double { 0 }
public func p0_c19(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64, _ a9: Double, _ a10: Double, _ a11: Double, _ a12: Double, _ a13: Double, _ a14: Double, _ a15: Double, _ a16: Double, _ a17: Double) -> Int64 { 0 }
public func p0_c20(_ a0: Double, _ a1: Double, _ a2: Double, _ a3: Double, _ a4: Double, _ a5: Double, _ a6: Double, _ a7: Double, _ a8: Double, _ a9: Int64, _ a10: Int64, _ a11: Int64, _ a12: Int64, _ a13: Int64, _ a14: Int64, _ a15: Int64, _ a16: Int64, _ a17: Int64) -> Int64 { 0 }
public func p0_c21(_ a0: Int64, _ a1: Double, _ a2: Int64, _ a3: Double, _ a4: Int64, _ a5: Double, _ a6: Int64, _ a7: Double, _ a8: Int64, _ a9: Double, _ a10: Int64, _ a11: Double, _ a12: Int64, _ a13: Double, _ a14: Int64, _ a15: Double, _ a16: Int64, _ a17: Double) -> Int64 { 0 }
public func p0_c22(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int8, _ a9: Int16, _ a10: Int32, _ a11: Int8, _ a12: Int64, _ a13: UInt8, _ a14: Int16, _ a15: Bool, _ a16: Float, _ a17: UInt16) -> Int64 { 0 }
public func p0_c23(_ a0: Int8, _ a1: Int16, _ a2: Int32, _ a3: Int64, _ a4: UInt8, _ a5: UInt16, _ a6: UInt32, _ a7: Bool) -> Int64 { 0 }
public func p0_c24(_ a0: Int8, _ a1: Int8, _ a2: Int8, _ a3: Int8, _ a4: Int8, _ a5: Int8, _ a6: Int8, _ a7: Int8, _ a8: Int8, _ a9: Int8, _ a10: Int8, _ a11: Int8) -> Int64 { 0 }
public func p0_c25(_ a0: Float, _ a1: Float, _ a2: Float, _ a3: Float, _ a4: Float, _ a5: Float, _ a6: Float, _ a7: Float, _ a8: Float, _ a9: Float, _ a10: Int8, _ a11: Int8, _ a12: Int8, _ a13: Int8, _ a14: Int8, _ a15: Int8, _ a16: Int8, _ a17: Int8, _ a18: Int8, _ a19: Int8) -> Int64 { 0 }
public func p0_c26(_ a0: P0DI, _ a1: P0FFI, _ a2: P0IFD) -> Int64 { 0 }
public func p0_c27(_ a0: P0D4, _ a1: P0D4, _ a2: Int64) -> Int64 { 0 }
public func p0_c28(_ a0: P0FF, _ a1: P0FD, _ a2: P0BHWQ, _ a3: P0BBH) -> Int64 { 0 }
public func p0_c29(_ a0: P0D5, _ a1: P0I5, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64) -> Int64 { 0 }
public func p0_c30(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: P0DI, _ a7: P0DI) -> Int64 { 0 }
public func p0_c31(_ a0: Double, _ a1: Double, _ a2: Double, _ a3: Double, _ a4: Double, _ a5: Double, _ a6: Double, _ a7: P0D4) -> Int64 { 0 }
public func p0_c32(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: P0BHWQ) -> Int64 { 0 }
public func p0_c33(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int8, _ a6: P0I2, _ a7: Int16) -> Int64 { 0 }
public func p0_c34(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: P0I4, _ a5: UInt8, _ a6: Float) -> Int64 { 0 }
public func p0_r01(_ a0: Int64, _ a1: Int64, _ a2: Int64) -> P0I2 { P0I2(f0: 0, f1: 0) }
public func p0_r02(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64) -> P0I4 { P0I4(f0: 0, f1: 0, f2: 0, f3: 0) }
public func p0_r03(_ a0: Double, _ a1: Double, _ a2: Double, _ a3: Double, _ a4: Double, _ a5: Double, _ a6: Double, _ a7: Double, _ a8: Int64) -> P0D4 { P0D4(f0: 0, f1: 0, f2: 0, f3: 0) }
public func p0_r04(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64) -> P0I5 { P0I5(f0: 0, f1: 0, f2: 0, f3: 0, f4: 0) }
public func p0_r05(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64, _ a9: Double, _ a10: Double, _ a11: Double, _ a12: Double, _ a13: Double, _ a14: Double, _ a15: Double, _ a16: Double, _ a17: Double) -> P0I8 { P0I8(f0: 0, f1: 0, f2: 0, f3: 0, f4: 0, f5: 0, f6: 0, f7: 0) }
public func p0_r06(_ a0: Int64, _ a1: Int64) -> P0DI { P0DI(f0: 0, f1: 0) }
public func p0_r07(_ a0: Int64, _ a1: Int64) {  }
public func p0_r08(_ a0: Int8, _ a1: Int16, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: Int64, _ a7: Int64, _ a8: Int64) -> P0FFI { P0FFI(f0: 0, f1: 0, f2: 0) }
public func p0_r09(_ a0: P0I3, _ a1: P0I3, _ a2: P0I3) -> P0I5 { P0I5(f0: 0, f1: 0, f2: 0, f3: 0, f4: 0) }
public func p0_r10(_ a0: Int64, _ a1: Int64, _ a2: Int64, _ a3: Int64, _ a4: Int64, _ a5: Int64, _ a6: UInt8, _ a7: Int32) -> P0IFD { P0IFD(f0: 0, f1: 0, f2: 0) }

extension P0I1 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I2 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I3 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I4 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I5 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I6 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I7 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0I8 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0DI {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0FFI {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0IFD {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0D4 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0D5 {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0FF {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0FD {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0BHWQ {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}

extension P0BBH {
    public func selfGet() -> Int64 { 0 }
    public func selfTake(_ x: Int64) -> Int64 { x }
    public mutating func selfMutate(_ x: Int64) { }
    public __consuming func selfConsume() -> Int64 { 0 }
    public static func selfStatic(_ x: Int64) -> Int64 { x }
    public var selfComputed: Int64 { get { 0 } set { } }
    public func selfThrows() throws -> Int64 { 0 }
}


// ---- getters that mutate self ----
@frozen public struct P0MutatingGetter {
    public var seed: Int32
    public lazy var derived: Int32 = seed &* 2
    public var counted: Int32 { mutating get { seed &+= 1; return seed } }
    public init(seed: Int32) { self.seed = seed }
}

public struct P0ResilientMutatingGetter {
    public var seed: Int32
    public lazy var derived: Int32 = seed &* 2
    public init(seed: Int32) { self.seed = seed }
}

// ---- a nested type named Type, which the ABI descriptor spells like a metatype ----
public final class P0TypeHost {
    public enum `Type` { case plain, fancy }
    public init() {}
    public func kind(_ flag: Bool) -> P0TypeHost.`Type` { flag ? .fancy : .plain }
    public func metatype() -> P0TypeHost.Type { P0TypeHost.self }
}

// A module's own top-level type named Type: no metatype is spelled the same way.
public enum `Type`: Int32 { case first = 1, second = 2 }
public func p0TopLevelType(_ t: `Type`) -> `Type` { t }

// ---- a nested tuple parameter: split element by element, all the way down ----
public func p0NestedTupleParameter(_ v: ((Int8, Int8), Int8)) -> Int8 { v.1 }

