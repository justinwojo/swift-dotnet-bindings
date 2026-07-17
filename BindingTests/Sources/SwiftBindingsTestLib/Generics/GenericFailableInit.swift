// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Failable initializers on GENERIC parents
//
// A failable `init?` projects to a static `TryCreate(..., out Self result)`. Every use of the
// parent's name inside that factory — the `out` parameter, the metadata lookup, and the
// construction expression — is a TYPE reference, so on a generic parent it must carry the
// parameter list. The bare leaf name has the wrong arity and binds to nothing; and when the
// module's namespace happens to share the type's simple name, the bare name resolves to the
// NAMESPACE instead, which is a different (and more confusing) error for the same root cause.
//
// Existing failable-init fixtures all have non-generic parents, so the arity axis was
// unexercised. Each type below keeps its initializer parameters non-generic so the shape under
// test is the parent's arity alone, not generic argument marshalling.

/// Generic reference type with a failable initializer — the class-cdecl factory path, where the
/// wrapper returns a nullable retained pointer and the factory constructs `new Self(handle)`.
public class Vault<Item> {
    public let capacity: Int32
    private var items: [Item] = []

    /// Failable init: returns nil for a non-positive capacity.
    public init?(capacity: Int32) {
        guard capacity > 0 else { return nil }
        self.capacity = capacity
    }

    public var storedCount: Int32 { Int32(items.count) }

    public var remainingCapacity: Int32 { capacity - Int32(items.count) }
}

/// Generic non-frozen struct with a failable initializer — projected as a C# class with an
/// opaque payload, so the factory copies the payload and constructs via the handle ctor.
public struct Journal<Entry> {
    public let limit: Int32
    private var entries: [Entry] = []

    /// Failable init: returns nil when the limit is out of range.
    public init?(limit: Int32) {
        guard limit > 0 && limit <= 1000 else { return nil }
        self.limit = limit
    }

    public var entryCount: Int32 { Int32(entries.count) }
}
