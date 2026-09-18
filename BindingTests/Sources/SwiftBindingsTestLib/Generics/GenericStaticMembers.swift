// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Static members declared on generic parents, one parent kind at a time.
//
// Swift passes these differently from instance members. A static on a generic struct or enum
// has a thin metatype `self` that is not passed at all; each generic parameter's OWN metadata
// follows the ordinary arguments, and a generic or address-only result goes through the
// indirect-result register. A static on a generic class instead takes the thick metatype as
// `self` and no separate generic-parameter metadata. A binding that calls any of these with the
// instance-member shape (parent metadata, no result buffer, no self register) compiles cleanly
// on both sides and then crashes or returns a wrong value, so each shape here asserts a real
// round-tripped value.

/// Generic enum: a static factory returning the enum itself, a static returning the bare
/// generic parameter, a static whose answer depends on the generic parameter's metadata, and a
/// static property.
public enum StaticChoice<Value> {
    case nothing
    case something(Value)

    /// Returns the enum through the indirect result and copies `value` through its value
    /// witnesses, so both the result buffer and the generic parameter's metadata matter.
    public static func wrapping(_ value: Value) -> StaticChoice<Value> { .something(value) }

    /// Indirect result with no arguments.
    public static func empty() -> StaticChoice<Value> { .nothing }

    /// Returns the bare generic parameter through the indirect result.
    public static func echo(_ value: Value) -> Value { value }

    /// Scalar result computed from the generic parameter's metadata alone.
    public static func valueSize() -> Int { MemoryLayout<Value>.size }

    /// Static computed property read through the generic parameter's metadata.
    public static var valueStride: Int { MemoryLayout<Value>.stride }

    public var boxed: Value? {
        if case .something(let v) = self { return v }
        return nil
    }

    public var isNothing: Bool {
        if case .nothing = self { return true }
        return false
    }
}

/// Generic struct: a static factory, a static returning the bare generic parameter, a
/// metadata-dependent scalar static, and a static property.
public struct StaticBox<T> {
    public let value: T

    public init(_ value: T) { self.value = value }

    /// Returns the struct through the indirect result.
    public static func make(_ value: T) -> StaticBox<T> { StaticBox(value) }

    /// Returns one of two generic arguments through the indirect result.
    public static func pick(_ first: T, _ second: T, takeFirst: Bool) -> T {
        takeFirst ? first : second
    }

    /// Scalar result computed from the generic parameter's metadata alone.
    public static func elementSize() -> Int { MemoryLayout<T>.size }

    /// Static computed property read through the generic parameter's metadata.
    public static var elementAlignment: Int { MemoryLayout<T>.alignment }

    /// Closure-bearing static: the closure's argument carries an error existential, which
    /// routes the member through the closure bridge rather than the ordinary wrapper.
    public static func reportSize(_ done: (Result<Int, Error>) -> Void) {
        done(.success(MemoryLayout<T>.size))
    }
}

/// Generic class: the metatype is `self`. Covers a plain static, a static returning an
/// instance, a static returning the bare generic parameter, an overridable `class func` and a
/// static property.
open class StaticGen<T> {
    public let seed: Int32

    public init() { self.seed = 5 }

    public init(seed: Int32) { self.seed = seed }

    /// Scalar result computed from the generic parameter's metadata, which a class static
    /// recovers from its metatype `self`.
    public static func elementSize() -> Int { MemoryLayout<T>.size }

    /// Returns a new instance of the generic class.
    public static func make(seed: Int32) -> StaticGen<T> { StaticGen<T>(seed: seed) }

    /// Returns the bare generic parameter through the indirect result.
    public static func echo(_ value: T) -> T { value }

    /// Overridable: dispatches on the metatype, so a subclass answers differently.
    open class func kind() -> Int32 { 1 }

    /// Static computed property read through the metatype.
    public static var elementStride: Int { MemoryLayout<T>.stride }

    /// Closure-bearing class static, reached through the closure bridge.
    public static func reportStride(_ done: (Result<Int, Error>) -> Void) {
        done(.success(MemoryLayout<T>.stride))
    }
}

/// Subclass overriding the class func, so dispatch through the subclass metatype reaches the
/// override.
public final class StaticGenSub<T>: StaticGen<T> {
    public override init() { super.init() }

    public override class func kind() -> Int32 { 2 }
}

/// Generic struct whose statics come from an extension that pins the generic parameter to a
/// concrete constructed generic (`PinnedPayload<Int32>`), the way a query type pins its result
/// to one forecast kind. Swift compiles those members with no generic parameters at all, and
/// the wrapper's unconditional conformance extension cannot name them.
public struct PinnedQuery<T> {
    public let tag: Int

    public init(tag: Int) { self.tag = tag }
}

/// The pin target. Generic itself so the pin is a constructed generic rather than a plain
/// nominal type.
public struct PinnedPayload<Element> {
    public let size: Int

    public init(size: Int) { self.size = size }
}

extension PinnedQuery where T == PinnedPayload<Int32> {
    /// Scalar result: no result buffer and no metadata, so the direct call reaches it as is.
    public static func doubled(_ value: Int) -> Int { value * 2 }

    /// Static property from the pinned extension, read the same way.
    public static var pinnedLimit: Int { 17 }

    /// Returns the non-frozen struct through the indirect result, which only a Swift-side
    /// wrapper could supply.
    public static func make(tag: Int) -> PinnedQuery<T> { PinnedQuery(tag: tag) }
}

/// Generic struct whose statics return types nested inside it. `Leaf` is a struct whose layout
/// Swift only knows through the outer's metadata, so the factory returns it through the
/// indirect-result register; `Node` is a class, returned as a plain reference.
public struct StaticNestOuter<T> {
    public struct Leaf {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }

    public final class Node {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }

    public static func makeLeaf(value: Int32) -> Leaf { Leaf(value: value) }
    public static func makeNode(value: Int32) -> Node { Node(value: value) }
}

/// Class bound for `StaticNarrowHolder`'s constrained extension.
open class StaticNarrowBase {
    public let tag: Int32
    public init(tag: Int32) { self.tag = tag }
}

/// Generic struct whose statics live in an extension that narrows the parameter to a class
/// (`where Base: StaticNarrowBase`). A superclass bound adds no witness table, so the statics
/// take the parameter's metadata and nothing else; the static-dispatch wrapper cannot see them
/// because its conformance extension carries no where-clause.
public struct StaticNarrowHolder<Base> {
    public init() {}
}

extension StaticNarrowHolder where Base: StaticNarrowBase {
    public static func scaled(_ value: Int32) -> Int32 { value * 3 }
    public static func makeBase(tag: Int32) -> StaticNarrowBase? { tag < 0 ? nil : StaticNarrowBase(tag: tag) }
}

/// Protocol whose static requirement a narrowed static reads, so the answer comes only from the
/// witness table.
public protocol StaticScaling {
    static var factor: Int32 { get }
}

public struct StaticScaleByThree: StaticScaling {
    public static var factor: Int32 { 3 }
    public init() {}
}

public struct StaticScaleByFive: StaticScaling {
    public static var factor: Int32 { 5 }
    public init() {}
}

/// Generic struct whose statics live in an extension narrowing the parameter to a protocol
/// (`where Scale: StaticScaling`). Swift passes the parameter's metadata and then its witness
/// table; the results below depend on the witness table alone, so a missing or wrong table reads
/// a different conformer's answer or faults.
public struct StaticScaledHolder<Scale> {
    public init() {}
}

extension StaticScaledHolder where Scale: StaticScaling {
    public static func scaled(_ value: Int32) -> Int32 { value * Scale.factor }
    public static var unit: Int32 { Scale.factor }
}

/// Generic struct with statics from an extension pinning the parameter to a plain nominal type
/// (`where T == Int32`), the non-generic counterpart of `PinnedQuery`.
public struct PlainPinnedQuery<T> {
    public init() {}
}

extension PlainPinnedQuery where T == Int32 {
    public static func tripled(_ value: Int) -> Int { value * 3 }
    public static var plainLimit: Int { 23 }
}
