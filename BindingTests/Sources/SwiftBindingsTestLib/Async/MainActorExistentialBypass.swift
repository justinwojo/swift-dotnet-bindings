// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential bypass on a @MainActor type
//
// A member whose only unbindable parameter is an existential with a default
// (`[any Equatable] = []`) is bound through ExistentialBypassEmitter: a wrapper that calls the
// member with the existential omitted. The wrapper calls a main-actor member synchronously, so it
// must itself be `@MainActor`, or swiftc refuses the call ("call to main actor-isolated instance
// method ... in a synchronous nonisolated context"). The class covers the method bypass (void and
// existential returns), the struct the initializer bypass.

@MainActor
public final class MainActorBypassSink {
    public private(set) var takes: Int32 = 0

    public init() {}

    /// Void method bypass: the C# member takes no argument and Swift supplies `[]`.
    public func take(_ filters: [any Equatable] = []) {
        takes += 1 + Int32(filters.count)
    }

    /// Existential-return method bypass.
    public func makeItem(_ filters: [any Equatable] = []) -> any Describable {
        takes += 1 + Int32(filters.count)
        return SimpleItem(id: "mab", label: "sink")
    }
}

@MainActor
public struct MainActorBypassConfig {
    public let filterCount: Int32
    public let label: String

    /// Initializer bypass: the C# factory takes the label only.
    public init(label: String, _ filters: [any Equatable] = []) {
        self.label = label
        self.filterCount = Int32(filters.count)
    }
}
