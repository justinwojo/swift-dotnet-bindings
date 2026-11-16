// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Generic methods of generic types, called directly
//
// Swift's polymorphic convention passes a method only the generic arguments it cannot recover from
// what it already has. A class instance carries its class's generic arguments and the witness tables
// of the conformances the class declares (in its isa), and an instance member of a generic struct or
// enum takes the parent's own metadata in its first metadata slot, which carries the same. So
// `ParentContextHolder<T: ParentContextTagged>.sizeOf<V>(_:)` takes (v, ParentContextHolder<T> metadata, V metadata,
// self), and `ParentContextBox<T>.sizeOf<V>(_:)` takes (v, V metadata, self): neither takes T's witness
// table. Each member reads V's metadata or V's witness table, so an argument in the wrong slot shows
// up as a wrong value rather than a silently ignored register.

public protocol ParentContextTagged {
    var contextTag: Int { get }
}

/// A one-word conformer.
public struct ParentContextTag: ParentContextTagged {
    public let contextTag: Int

    public init(contextTag: Int) { self.contextTag = contextTag }
}

/// A two-word conformer whose tag reads both words.
public struct ParentContextWide: ParentContextTagged {
    public let low: Int
    public let high: Int

    public init(low: Int, high: Int) {
        self.low = low
        self.high = high
    }

    public var contextTag: Int { low * 100 + high }
}

public struct ParentContextHolder<T: ParentContextTagged> {
    public var seed: Int

    public init(seed: Int) { self.seed = seed }

    /// Whether the holder is seeded; the value argument is only passed.
    public func store<V>(_ v: V) -> Bool { seed > 0 }

    /// V's size plus the seed: reads V's metadata.
    public func sizeOf<V>(_ v: V) -> Int { MemoryLayout<V>.size + seed }

    /// V's tag plus the seed: reads V's witness table.
    public func tagOf<V: ParentContextTagged>(_ v: V) -> Int { v.contextTag + seed }
}

public struct ParentContextPair<A: ParentContextTagged, B> {
    public var seed: Int

    public init(seed: Int) { self.seed = seed }

    /// V's size plus the seed: V's metadata follows the pair's own, with nothing for B between them.
    public func sizeOf<V>(_ v: V) -> Int { MemoryLayout<V>.size + seed }
}

public enum ParentContextChoice<T: ParentContextTagged> {
    case seeded(Int)
    case unseeded

    public static func make(seed: Int) -> ParentContextChoice<T> { .seeded(seed) }

    public var seed: Int {
        if case .seeded(let seed) = self { return seed }
        return 0
    }

    /// Whether the choice is seeded; the value argument is only passed.
    public func store<V>(_ v: V) -> Bool { seed > 0 }

    /// V's size plus the seed: reads V's metadata.
    public func sizeOf<V>(_ v: V) -> Int { MemoryLayout<V>.size + seed }
}

public final class ParentContextBox<T: ParentContextTagged> {
    public var seed: Int

    public init(seed: Int) { self.seed = seed }

    /// Whether the box is seeded; the value argument is only passed.
    public func store<V>(_ v: V) -> Bool { seed > 0 }

    /// V's size plus the seed: reads V's metadata.
    public func sizeOf<V>(_ v: V) -> Int { MemoryLayout<V>.size + seed }

    /// V's tag plus the seed: reads V's witness table.
    public func tagOf<V: ParentContextTagged>(_ v: V) -> Int { v.contextTag + seed }
}

open class ParentContextOpenBox<T: ParentContextTagged> {
    public var seed: Int

    public init(seed: Int) { self.seed = seed }

    /// V's tag plus the seed, through the class's dispatch thunk: reads V's witness table.
    open func tagOf<V: ParentContextTagged>(_ v: V) -> Int { v.contextTag + seed }
}
