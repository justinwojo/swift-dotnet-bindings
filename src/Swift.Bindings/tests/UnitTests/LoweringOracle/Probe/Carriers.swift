// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Every carrier shape the lowering models, once as a parameter and once as a result, plus the
// layouts that are easy to get wrong: optionals, nested structs, no-payload enums, existentials,
// function values, metatypes and tuples.

import Foundation
import CoreGraphics
import CoreMedia

public final class CarrierClass {
    public init() {}
}

public protocol CarrierProtocol {}
public protocol CarrierClassProtocol: AnyObject {}
@objc public protocol CarrierObjCProtocol {}

@frozen public enum CarrierEnum { case a, b, c }
@frozen public enum CarrierOneCase { case only }
public enum CarrierResilientEnum { case a, b }
public struct CarrierResilient { public var x: Int; public init() { x = 0 } }

@frozen public struct CarrierInner { public var a: Int8; public var b: Int64 }
@frozen public struct CarrierOuter { public var x: Int8; public var inner: CarrierInner }
@frozen public struct CarrierOptionalDouble { public var d: Double?; public var i: Int8 }
@frozen public struct CarrierWithString { public var s: String; public var n: Int32 }
@frozen public struct CarrierWithClass { public var k: CarrierClass; public var flag: Bool }
@frozen public struct CarrierWithEnum { public var e: CarrierEnum; public var n: Int16; public var m: Int32 }
@frozen public struct CarrierWithResilient { public var r: CarrierResilient }
@frozen public struct CarrierEmpty { }
@frozen public struct CarrierInts { public var a: Int64; public var b: Int64 }

// ---- scalars and standard-library carriers ----
public func carrierBool(_ a: Bool) -> Bool { a }
public func carrierInt8(_ a: Int8, _ b: UInt8) -> Int8 { a }
public func carrierInt16(_ a: Int16, _ b: UInt16) -> UInt16 { b }
public func carrierInt32(_ a: Int32, _ b: UInt32) -> Int32 { a }
public func carrierInt(_ a: Int, _ b: UInt) -> UInt { b }
public func carrierFloat(_ a: Float, _ b: Double) -> Float { a }
public func carrierString(_ a: String) -> String { a }
public func carrierCharacter(_ a: Character) -> Character { a }
public func carrierSubstring(_ a: Substring) -> Substring { a }
public func carrierArray(_ a: [Int]) -> [String] { [] }
public func carrierDictionary(_ a: [String: Int]) -> [String: Int] { a }
public func carrierSet(_ a: Set<Int>) -> Set<Int> { a }
public func carrierContiguousArray(_ a: ContiguousArray<Int>) -> ContiguousArray<Int> { a }
public func carrierUnicodeScalar(_ a: Unicode.Scalar) -> Unicode.Scalar { a }

// ---- pointers ----
public func carrierRawPointer(_ a: UnsafeRawPointer, _ b: UnsafeMutableRawPointer) -> UnsafeRawPointer { a }
public func carrierTypedPointer(_ a: UnsafePointer<Int>, _ b: UnsafeMutablePointer<Double>) -> UnsafeMutablePointer<Double> { b }
public func carrierOpaquePointer(_ a: OpaquePointer) -> OpaquePointer { a }
public func carrierOptionalPointer(_ a: UnsafeRawPointer?) -> UnsafeMutableRawPointer? { nil }
public func carrierUnmanaged(_ a: Unmanaged<CarrierClass>) -> Unmanaged<CarrierClass> { a }
public func carrierBufferPointer(_ a: UnsafeBufferPointer<Int>) -> UnsafeMutableBufferPointer<Int> { UnsafeMutableBufferPointer(start: nil, count: 0) }
public func carrierRawBufferPointer(_ a: UnsafeRawBufferPointer) -> UnsafeMutableRawBufferPointer { UnsafeMutableRawBufferPointer(start: nil, count: 0) }

// ---- Foundation and CoreGraphics ----
public func carrierData(_ a: Data) -> Data { a }
public func carrierURL(_ a: URL) -> URL { a }
public func carrierDate(_ a: Date) -> Date { a }
public func carrierUUID(_ a: UUID) -> UUID { a }
public func carrierDecimal(_ a: Decimal) -> Decimal { a }
public func carrierNSRange(_ a: NSRange) -> NSRange { a }
public func carrierCGFloat(_ a: CGFloat) -> CGFloat { a }
public func carrierCGPoint(_ a: CGPoint) -> CGSize { .zero }
public func carrierCGRect(_ a: CGRect) -> CGRect { a }
public func carrierCGVector(_ a: CGVector) -> CGVector { a }

// ---- optionals ----
public func carrierOptionalInt(_ a: Int?) -> Int? { a }
public func carrierOptionalDouble(_ a: Double?) -> Double? { a }
public func carrierOptionalBool(_ a: Bool?) -> Bool? { a }
public func carrierOptionalInt8(_ a: Int8?) -> Int32? { nil }
public func carrierOptionalString(_ a: String?) -> String? { a }
public func carrierOptionalClass(_ a: CarrierClass?) -> CarrierClass? { a }
public func carrierOptionalEnum(_ a: CarrierEnum?) -> CarrierEnum? { a }
public func carrierOptionalStruct(_ a: CarrierInts?) -> CarrierInts? { a }
public func carrierOptionalEmpty(_ a: CarrierEmpty?) -> CarrierEmpty? { a }
public func carrierOptionalArray(_ a: [Int]?) -> [Int]? { a }
public func carrierOptionalClosure(_ a: (() -> Void)?) -> ((Int) -> Int)? { nil }

// ---- structs and enums ----
public func carrierOuter(_ a: CarrierOuter) -> CarrierOuter { a }
public func carrierOptionalDoubleStruct(_ a: CarrierOptionalDouble) -> CarrierOptionalDouble { a }
public func carrierWithString(_ a: CarrierWithString) -> CarrierWithString { a }
public func carrierWithClass(_ a: CarrierWithClass) -> CarrierWithClass { a }
public func carrierWithEnum(_ a: CarrierWithEnum) -> CarrierWithEnum { a }
public func carrierWithResilient(_ a: CarrierWithResilient) -> CarrierWithResilient { a }
public func carrierEmpty(_ a: CarrierEmpty, _ b: Int) -> CarrierEmpty { a }
public func carrierEnum(_ a: CarrierEnum) -> CarrierEnum { a }
public func carrierOneCase(_ a: CarrierOneCase, _ b: Int) -> CarrierOneCase { a }
public func carrierResilientEnum(_ a: CarrierResilientEnum) -> CarrierResilientEnum { a }
public func carrierResilient(_ a: CarrierResilient) -> CarrierResilient { a }

// ---- references and existentials ----
public func carrierClass(_ a: CarrierClass) -> CarrierClass { a }
public func carrierAnyObject(_ a: AnyObject) -> AnyObject { a }
public func carrierAny(_ a: Any) -> Any { a }
public func carrierExistential(_ a: any CarrierProtocol) -> any CarrierProtocol { a }
public func carrierClassExistential(_ a: any CarrierClassProtocol) -> any CarrierClassProtocol { a }
public func carrierObjCExistential(_ a: any CarrierObjCProtocol) -> any CarrierObjCProtocol { a }
public func carrierComposition(_ a: any CarrierClassProtocol & CarrierProtocol) -> Int { 0 }
public func carrierOpaqueResult() -> some CarrierProtocol { CarrierConformer() }
public struct CarrierConformer: CarrierProtocol {}

// ---- function values ----
public func carrierClosure(_ a: @escaping (Int) -> Int) -> (Int) -> Int { a }
public func carrierNonEscaping(_ a: (Int) -> Int) -> Int { a(0) }
public func carrierThrowingClosure(_ a: @escaping () throws -> Void) {}
public func carrierCFunction(_ a: @convention(c) (Int32) -> Int32) -> Int32 { a(0) }
public func carrierThin(_ a: @convention(thin) () -> Void) {}

// ---- metatypes ----
public func carrierClassMetatype(_ a: CarrierClass.Type) -> CarrierClass.Type { a }
public func carrierStructMetatype(_ a: CarrierInts.Type, _ b: Int) -> Int { b }
public func carrierExistentialMetatype(_ a: Any.Type) -> Any.Type { a }

// ---- tuples ----
public func carrierTupleParameter(_ a: (Int, Double)) -> Int { 0 }
public func carrierTupleResult() -> (Int, Double) { (0, 0) }
public func carrierWideTupleResult() -> (Int, Int, Int, Int, Int) { (0, 0, 0, 0, 0) }
public func carrierMixedTupleResult() -> (Int8, Int32, Double) { (0, 0, 0) }

// ---- conventions ----
public func carrierInout(_ a: inout Int, _ b: inout CarrierInts, _ c: inout String) {}
public func carrierOwned(_ a: __owned String, _ b: __owned CarrierWithResilient, _ c: __owned [Int]) {}
public func carrierVariadic(_ a: Int...) -> Int { a.count }
public func carrierThrows(_ a: Int) throws -> Int { a }
public func carrierThrowsWide(_ a: Int) throws -> CarrierResilient { CarrierResilient() }
public func carrierThrowsVoid() throws {}

// ---- classes ----
public class CarrierOpenClass {
    public var stored: Int = 0
    public init() {}
    public init(value: Int) { stored = value }
    public func method(_ a: Int) -> Int { a }
    public func methodWide(_ a: CarrierResilient) -> CarrierOuter { CarrierOuter(x: 0, inner: CarrierInner(a: 0, b: 0)) }
    public func methodThrows() throws -> Int { 0 }
    public class func classMethod(_ a: Int) -> Int { a }
    public static func staticMethod(_ a: Double) -> Double { a }
    public final func finalMethod() -> String { "" }
    public var computed: CarrierInts { get { CarrierInts(a: 0, b: 0) } set {} }
    public subscript(_ i: Int) -> String { get { "" } set {} }
}

public final class CarrierFailable {
    public init?(value: Int) { if value < 0 { return nil } }
}

public struct CarrierFailableStruct {
    public var x: Int
    public init?(value: Int) { if value < 0 { return nil }; x = value }
}

@frozen public struct CarrierFrozenFailable {
    public var x: Int
    public init?(value: Int) { if value < 0 { return nil }; x = value }
    public subscript(_ i: Int) -> Int { get { x } set { x = newValue } }
}

// ---- @objc enums: C enums stored as their raw value, frozen or not ----
@objc public enum CarrierObjCEnum: Int { case a = 10, b = 20 }
@objc public enum CarrierObjCEnum32: Int32 { case a = 1, b = 2 }
public func carrierObjCEnum(_ a: CarrierObjCEnum) -> CarrierObjCEnum { a }
public func carrierObjCEnum32(_ a: CarrierObjCEnum32) -> CarrierObjCEnum32 { a }
public func carrierOptionalObjCEnum(_ a: CarrierObjCEnum?) -> CarrierObjCEnum32? { nil }
extension CarrierObjCEnum {
    public var doubled: Int { rawValue * 2 }
}

// ---- the Error existential: boxed, so one reference ----
public func carrierError(_ a: any Error) -> any Error { a }
public func carrierOptionalError(_ a: (any Error)?) -> (any Error)? { a }
@frozen public struct CarrierWithError { public var e: (any Error)?; public var n: Int32 }
public func carrierWithError(_ a: CarrierWithError) -> CarrierWithError { a }

// ---- a C struct packed below its natural alignment ----
@frozen public struct CarrierTaggedTime { public var tag: Int32; public var time: CMTime }
public func carrierCMTime(_ a: CMTime) -> CMTime { a }
public func carrierTaggedTime(_ a: CarrierTaggedTime) -> Int32 { a.tag }

// ---- ranges: two bounds ----
@frozen public struct CarrierWithRange { public var bounds: ClosedRange<Int>?; public var marker: Int }
public func carrierRange(_ a: Range<Int>) -> ClosedRange<Int32> { 0...1 }
public func carrierWithRange(_ a: CarrierWithRange) -> Int { a.marker }

// ---- a frozen @objc enum: every raw value is a case, so Optional adds a tag byte ----
@objc @frozen public enum CarrierFrozenObjCEnum: Int { case a = 10, b = 20 }
public func carrierOptionalFrozenObjCEnum(_ a: CarrierFrozenObjCEnum?) -> CarrierFrozenObjCEnum? { a }

// ---- an @objc enum aligned above its raw type ----
@_alignment(16) @objc @frozen public enum CarrierAlignedObjCEnum: Int32 { case a = 1, b = 2 }
@frozen public struct CarrierWithAlignedObjCEnum { public var tag: Int8; public var e: CarrierAlignedObjCEnum }
public func carrierWithAlignedObjCEnum(_ a: CarrierWithAlignedObjCEnum) -> Int8 { a.tag }
