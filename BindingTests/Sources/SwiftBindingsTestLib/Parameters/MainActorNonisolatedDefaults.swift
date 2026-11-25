// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Default arguments on nonisolated members of a @MainActor type
//
// A trimmed overload of a member with a default that C# cannot spell (`String(repeating:count:)`)
// calls a Swift shim in an extension of the member's type, and that shim calls the member without
// the trimmed arguments, so Swift evaluates its own defaults. An extension of a @MainActor type
// inherits the type's isolation, while the overload's @_cdecl wrapper follows the member: for a
// `nonisolated` member the wrapper is nonisolated, so the shim must opt out too, or swiftc refuses
// the wrapper's synchronous call to it and every trimmed overload of the member is withdrawn.

/// A main-actor class whose initializer, instance method and static method are `nonisolated` and
/// take a default C# cannot express. `isolatedLabel` keeps the class's isolation, as a control.
@MainActor
public final class MainActorNonisolatedDefaults {
    public let name: String
    public let token: String

    nonisolated public init(name: String, token: String = String(repeating: "auto", count: 2)) {
        self.name = name
        self.token = token
    }

    nonisolated public func label(prefix: String, suffix: String = String(repeating: "end", count: 1)) -> String {
        return "\(prefix):\(name):\(token):\(suffix)"
    }

    nonisolated public static func make(tag: String = String(repeating: "tag", count: 1)) -> String {
        return "made:\(tag)"
    }

    public func isolatedLabel(prefix: String, suffix: String = String(repeating: "main", count: 1)) -> String {
        return "\(prefix)@\(name):\(suffix)"
    }
}
