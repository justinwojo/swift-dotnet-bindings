// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Types named after Swift's reserved member names
//
// A library may declare a top-level class called `Protocol`. That is a legal declaration, but a
// bare `Protocol` in a wrapper means the metatype, not the library's type, so every spelling has
// to be escaped or module-qualified.

open class Protocol: Hashable {
    public let value: String

    public init(_ value: String) { self.value = value }

    public static func == (lhs: Protocol, rhs: Protocol) -> Bool { lhs.value == rhs.value }

    public func hash(into hasher: inout Hasher) { hasher.combine(value) }
}

open class ReservedRegistry<T: Hashable> {
    public var items: [T] = []

    public init() {}

    public func add(_ item: T) { items.append(item) }

    public var count: Int { items.count }
}

public final class ReservedWhitelist {
    public var protocols: ReservedRegistry<Protocol> = ReservedRegistry()

    public init() {}

    public func addProtocol(_ value: Protocol) -> ReservedWhitelist {
        protocols.add(value)
        return self
    }

    public func first() -> Protocol? { protocols.items.first }

    public var protocolCount: Int { protocols.count }
}
