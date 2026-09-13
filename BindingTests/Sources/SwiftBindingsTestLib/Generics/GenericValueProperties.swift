// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

@frozen
public struct GvpConcretePair {
    public let first: Int32
    public let second: Int32

    public init(first: Int32, second: Int32) {
        self.first = first
        self.second = second
    }
}

/// A generic nominal whose stored layout is independent of `T`. Its generated C# projection is
/// a value type, so every instance property must borrow the caller's actual inline storage rather
/// than a SafeHandle payload. `next` is a mutating getter and proves native write-back.
@frozen
public struct GvpScalar<T: SlotWeight> {
    private var current: Int32
    private var storedCount: Int32

    public init(count: Int32, current: Int32) {
        self.storedCount = count
        self.current = current
    }

    public var count: Int32 {
        get { storedCount }
        set { storedCount = newValue }
    }

    public var next: Int32 {
        mutating get {
            current += 1
            return current
        }
    }

    public var valid: Bool { storedCount >= 0 }
    public var pair: GvpConcretePair { GvpConcretePair(first: storedCount, second: current) }
    public var label: String { "\(storedCount):\(current)" }
}

public func makeGvpScalarLight(count: Int32, current: Int32) -> GvpScalar<LightSlot> {
    GvpScalar<LightSlot>(count: count, current: current)
}

public func makeGvpScalarHeavy(count: Int32, current: Int32) -> GvpScalar<HeavySlot> {
    GvpScalar<HeavySlot>(count: count, current: current)
}

// MARK: - Payload-backed generic parents and composite generic properties

public final class GvpTrackedRef {
    private static var live: Int32 = 0
    private static var deinits: Int32 = 0

    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
        Self.live += 1
    }

    deinit {
        Self.live -= 1
        Self.deinits += 1
    }

    public static var liveCount: Int32 { live }
    public static var deinitCount: Int32 { deinits }

    public static func resetCounters() {
        live = 0
        deinits = 0
    }
}

@frozen
public struct GvpPair<A, B> {
    public let first: A
    public let second: B

    public init(first: A, second: B) {
        self.first = first
        self.second = second
    }
}

/// Non-frozen, payload-backed control. Every property is reached through the open generic
/// accessor; no closed-conformer specialization is needed for these unconstrained parents.
public struct GvpOpaque<T> {
    private var current: Int32
    private var storedCount: Int32
    private var storedItem: T
    private var storedMaybe: T?
    private var storedItems: [T]
    private var storedByName: [String: T]
    private var storedPair: GvpPair<T, T>

    public init(first: T, second: T, count: Int32) {
        current = 0
        storedCount = count
        storedItem = first
        storedMaybe = nil
        storedItems = [first, second]
        storedByName = ["first": first, "second": second]
        storedPair = GvpPair(first: first, second: second)
    }

    public var count: Int32 {
        get { storedCount }
        set { storedCount = newValue }
    }
    public var label: String { "opaque:\(storedCount)" }
    public var next: Int32 {
        mutating get {
            current += 1
            return current
        }
    }
    public var item: T {
        get { storedItem }
        set { storedItem = newValue }
    }
    public var maybe: T? {
        get { storedMaybe }
        set { storedMaybe = newValue }
    }
    public var items: [T] {
        get { storedItems }
        set { storedItems = newValue }
    }
    public var byName: [String: T] {
        get { storedByName }
        set { storedByName = newValue }
    }
    public var pair: GvpPair<T, T> { storedPair }
}

/// Frozen but reference-bearing control. Storing T and String keeps its managed projection
/// payload-backed, proving that `@frozen` alone is not used as the inline-receiver oracle.
@frozen
public struct GvpRefFrozen<T> {
    private var storedItem: T
    private var storedCount: Int32
    private var current: Int32
    private let prefix: String

    public init(item: T, count: Int32, prefix: String) {
        storedItem = item
        storedCount = count
        current = 0
        self.prefix = prefix
    }

    public var count: Int32 {
        get { storedCount }
        set { storedCount = newValue }
    }
    public var label: String { "\(prefix):\(storedCount)" }
    public var next: Int32 {
        mutating get {
            current += 1
            return current
        }
    }
    public var item: T {
        get { storedItem }
        set { storedItem = newValue }
    }
    public var maybe: T? { storedItem }
    public var items: [T] { [storedItem] }
}

public func makeGvpOpaqueTracked(first: GvpTrackedRef, second: GvpTrackedRef, count: Int32)
    -> GvpOpaque<GvpTrackedRef> {
    GvpOpaque(first: first, second: second, count: count)
}

public func makeGvpRefFrozenTracked(item: GvpTrackedRef, count: Int32, prefix: String)
    -> GvpRefFrozen<GvpTrackedRef> {
    GvpRefFrozen(item: item, count: count, prefix: prefix)
}

// MARK: - Metadata/PWT slot boundaries

public struct GvpTriple<A, B, C> {
    public init(_ a: A, _ b: B, _ c: C) {}
    public var count: Int32 { 3 }
}

public func makeGvpTripleTracked(
    first: GvpTrackedRef, second: GvpTrackedRef, third: GvpTrackedRef
) -> GvpTriple<GvpTrackedRef, GvpTrackedRef, GvpTrackedRef> {
    GvpTriple(first, second, third)
}
