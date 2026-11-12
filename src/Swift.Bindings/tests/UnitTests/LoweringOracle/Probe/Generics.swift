// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Generic callees: where swiftc places each generic parameter's metadata and each conformance's
// witness table, and which of them a self or a class argument carries instead.

import Foundation

public protocol GenericProtocol {
    func requirement() -> Int
    func requirementTaking(_ x: Int, _ y: Self) -> Self
    static func staticRequirement() -> Int
    init(seed: Int)
    var property: Int { get set }
}

public protocol GenericClassProtocol: AnyObject {
    func classRequirement() -> Int
}

@objc public protocol GenericObjCProtocol {}

open class GenericBase {
    public init() {}
}

public final class GenericDerived: GenericBase, GenericClassProtocol {
    public func classRequirement() -> Int { 0 }
}

public struct GenericConformer: GenericProtocol {
    public init(seed: Int) {}
    public func requirement() -> Int { 0 }
    public func requirementTaking(_ x: Int, _ y: GenericConformer) -> GenericConformer { y }
    public static func staticRequirement() -> Int { 0 }
    public var property: Int { get { 0 } set {} }
}

// ---- free generic functions ----
public func genericIdentity<T>(_ t: T) -> T { t }
public func genericConstrained<T: GenericProtocol>(_ t: T) -> Int { 0 }
public func genericAnyObject<T: AnyObject>(_ t: T) -> T { t }
public func genericSuperclass<T: GenericBase>(_ t: T) -> T { t }
public func genericClassProtocol<T: GenericClassProtocol>(_ t: T) -> T { t }
public func genericObjCProtocol<T: GenericObjCProtocol>(_ t: T) -> Int { 0 }
public func genericTwo<T, U: GenericProtocol>(_ t: T, _ u: U) -> Int { 0 }
public func genericPairResult<T>(_ t: T) -> (T, T) { (t, t) }
public func genericArray<T>(_ a: [T]) -> [T] { a }
public func genericMetatype<T>(_ t: T.Type) -> Int { 0 }
public func genericOptional<T>(_ t: T?) -> T? { t }
public func genericHashable<T: Hashable>(_ t: T) -> Int { 0 }
public func genericComparable<T: Comparable>(_ a: T, _ b: T) -> Bool { a < b }
public func genericSendable<T: Sendable>(_ t: T) -> Int { 0 }
public func genericWhere<T>(_ t: T) -> Int where T: GenericProtocol, T: Sendable { 0 }
public func genericTwoConformances<T: GenericProtocol & Hashable>(_ t: T) -> Int { 0 }
public func genericInout<T>(_ t: inout T) {}
public func genericThrows<T>(_ t: T) throws -> T { t }
public func genericClosure<T>(_ f: @escaping (T) -> Int) -> Int { 0 }
public func genericInClass<T>(_ k: GenericClass<T>) -> Int { 0 }
public func genericNested<T>(_ k: [GenericClass<T>]) -> Int { 0 }

// ---- a generic frozen struct ----
@frozen public struct GenericFrozen<T> {
    public var value: T
    public var tag: Int
    public init(value: T, tag: Int) { self.value = value; self.tag = tag }
    public func get() -> T { value }
    public func getTag() -> Int { tag }
    public mutating func set(_ v: T) { value = v }
    public __consuming func take() -> T { value }
    public static func make(_ v: T) -> GenericFrozen<T> { GenericFrozen(value: v, tag: 0) }
    public func map<U>(_ f: (T) -> U) -> GenericFrozen<U> { GenericFrozen<U>(value: f(value), tag: tag) }
    public func pair<U: GenericProtocol>(_ u: U) -> Int { 0 }
    public var computed: Int { get { tag } set { tag = newValue } }
    public subscript(_ i: Int) -> T { get { value } set { value = newValue } }
}

extension GenericFrozen where T: GenericProtocol {
    public func constrained() -> Int { value.requirement() }
}

// ---- a resilient generic struct with a constrained parameter ----
public struct GenericResilient<T: GenericProtocol> {
    public var value: T
    public init(value: T) { self.value = value }
    public func get() -> T { value }
    public static func make() -> Int { 0 }
    public func with<U: Hashable>(_ u: U) -> Int { 0 }
}

// ---- a generic class ----
open class GenericClass<T> {
    public var value: T
    public init(_ value: T) { self.value = value }
    open func get() -> T { value }
    public final func finalGet() -> T { value }
    open func with<U>(_ u: U) -> Int { 0 }
    open class func make(_ v: T) -> GenericClass<T> { GenericClass(v) }
    public static func count() -> Int { 0 }
}

// ---- protocol extensions ----
extension GenericProtocol {
    public func extensionMethod() -> Int { requirement() }
    public static func extensionStatic() -> Int { 0 }
    public func extensionGeneric<U>(_ u: U) -> Int { 0 }
}

extension GenericClassProtocol {
    public func classExtensionMethod() -> Int { classRequirement() }
}

// ---- class-boundness inherited through refinement ----
public protocol GenericRefinedClassProtocol: GenericClassProtocol {
    func refinedRequirement(_ x: Int32) -> Int32
}
public func genericRefinedExistential(_ x: any GenericRefinedClassProtocol) -> Int { 0 }
public func genericRefinedOptional(_ x: (any GenericRefinedClassProtocol)?) -> (any GenericRefinedClassProtocol)? { x }
public func genericRefined<T: GenericRefinedClassProtocol>(_ t: T) -> T { t }
extension GenericRefinedClassProtocol {
    public func refinedExtension() -> Int { 0 }
}

// ---- a generic frozen struct whose layout does not depend on its parameter ----
@frozen public struct GenericFixedLayout<T: GenericProtocol> {
    public var a: Int32
    public var b: Int32
    public init(a: Int32, b: Int32) { self.a = a; self.b = b }
    public func sum() -> Int32 { a &+ b }
    public var total: Int32 { a &+ b }
    public subscript(_ i: Int32) -> Int32 { a &+ i }
    public mutating func bump() { a &+= 1 }
    public func with<U: Hashable>(_ u: U) -> Int32 { a }
    public static func make() -> GenericFixedLayout<T> { GenericFixedLayout(a: 0, b: 0) }
}

@frozen public struct GenericClassBoundLayout<T: GenericClassProtocol> {
    public var t: T
    public var n: Int
    public init(t: T, n: Int) { self.t = t; self.n = n }
    public func get() -> T { t }
}

// ---- metadata read from a class argument's nested generic arguments ----
public func genericInClassOptional<T>(_ k: GenericClass<T?>) -> Int { 0 }
public func genericInClassArray<T>(_ k: GenericClass<[T]>) -> Int { 0 }
public func genericKeyPathOptional<R, V>(_ k: KeyPath<R, V?>) -> Int { 0 }

// ---- a class member's dynamic Self ----
open class GenericSelfReturning {
    public required init() {}
    open func copy() -> Self { Self() }
    public class func make() -> Self { Self() }
}

// ---- an inout class argument: its metadata is not read, so T's is passed ----
public func genericInoutClass<T>(_ k: inout GenericClass<T>) -> Int { 0 }

// ---- a class nested in a generic class: its metadata carries every enclosing argument ----
open class GenericOuter<T> {
    open class Inner<U> {
        public init() {}
    }
}
public func genericNestedClass<T, U>(_ k: GenericOuter<T>.Inner<U>) -> Int { 0 }
public func genericNestedClassArgument<T, U>(_ k: GenericClass<GenericOuter<T>.Inner<U>>) -> Int { 0 }

// ---- a marker protocol: a conformance with no witness table ----
@_marker public protocol GenericMarker {}
public func genericMarker<T: GenericMarker>(_ v: T) -> Int { 0 }
