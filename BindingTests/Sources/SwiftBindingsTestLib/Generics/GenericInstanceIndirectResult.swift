// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Instance members returning a non-frozen value type built from generic parameters.
//
// Swift returns a non-frozen struct through the indirect-result register whatever its size,
// because a library-evolution client cannot know its layout. A member that the static-dispatch
// wrapper turns down falls back to the direct P/Invoke, which declares the result by value and
// passes no result buffer: the callee writes through an unset register and the managed side
// adopts its own stack slot as the value. Each shape here mirrors a query-builder or
// async-sequence API that took that route, and each test asserts a round-tripped value.

/// The result: a non-frozen generic struct, so it always comes back through the indirect result.
public struct IndirectRequest<T> {
    public let tag: Int
    public let note: String

    public init(tag: Int, note: String) {
        self.tag = tag
        self.note = note
    }
}

/// A non-frozen struct parameter bound over the caller's generic parameter.
public struct IndirectAggregate<T> {
    public let weight: Int

    public init(weight: Int) { self.weight = weight }
}

/// A class parameter bound over the caller's generic parameter.
public final class IndirectAlias<T> {
    public let offset: Int

    public init(offset: Int) { self.offset = offset }
}

/// Generic struct whose instance members take bound-generic arguments, return a type nested in
/// it, or declare a generic parameter of their own.
public struct IndirectTable<T> {
    public let tag: Int

    public init(tag: Int) { self.tag = tag }

    /// Struct parameter bound over `T`, non-frozen result bound over `T`.
    public func having(_ aggregate: IndirectAggregate<T>) -> IndirectRequest<T> {
        IndirectRequest(tag: tag * 100 + aggregate.weight, note: "having")
    }

    /// Class parameter bound over `T`, non-frozen result bound over `T`.
    public func aliased(_ alias: IndirectAlias<T>) -> IndirectRequest<T> {
        IndirectRequest(tag: tag * 100 + alias.offset, note: "aliased")
    }

    /// A struct nested in the generic parent. Its metadata comes from the parent's, so it is
    /// returned through the indirect result like any other non-frozen generic value.
    public struct Cursor {
        public let position: Int
        public let label: String

        public init(position: Int, label: String) {
            self.position = position
            self.label = label
        }

        public func advanced(by step: Int) -> Int { position + step }
    }

    public func makeCursor() -> Cursor { Cursor(position: tag, label: "cursor") }

    /// A member generic of its own. The static-dispatch wrapper has no way to spell a member
    /// generic, so this one has no sound route and must say so.
    public func with<U>(_ other: U) -> IndirectRequest<T> {
        IndirectRequest(tag: tag, note: "with \(other)")
    }
}

/// Non-generic class whose member generic returns a non-frozen struct bound over it.
public final class IndirectSource {
    public init() {}

    public func wrap<U>(_ value: U, tag: Int) -> IndirectRequest<U> {
        IndirectRequest(tag: tag, note: "wrap \(value)")
    }
}

/// Free function with the same shape.
public func makeIndirectRequest<U>(_ value: U, tag: Int) -> IndirectRequest<U> {
    IndirectRequest(tag: tag, note: "free \(value)")
}

/// A protocol with a static requirement, so a member of a parent constrained to it reads the
/// requirement through the witness table.
public protocol IndirectRanked {
    static var rank: Int { get }
}

public struct IndirectRankSeven: IndirectRanked {
    public static var rank: Int { 7 }
    public init() {}
}

/// Generic struct whose parameter carries a protocol constraint: the nested-type result depends
/// on both the parameter's metadata and its witness table.
public struct IndirectRankedTable<Base: IndirectRanked> {
    public init() {}

    public struct Cursor {
        public let position: Int
        public init(position: Int) { self.position = position }

        /// Reads the rank through the parent parameter's witness table.
        public func ranked() -> Int { position * Base.rank }
    }

    public func makeCursor() -> Cursor { Cursor(position: Base.rank * 3) }
}
