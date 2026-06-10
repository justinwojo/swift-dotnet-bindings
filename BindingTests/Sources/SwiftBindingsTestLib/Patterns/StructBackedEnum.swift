// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Struct-Backed Enum

/// Struct with static let properties simulating an enum, plus rawValue.
public struct HttpVerb: Equatable {
    public var rawValue: String

    public init(rawValue: String) {
        self.rawValue = rawValue
    }

    public static let get = HttpVerb(rawValue: "GET")
    public static let post = HttpVerb(rawValue: "POST")
    public static let put = HttpVerb(rawValue: "PUT")
    public static let delete = HttpVerb(rawValue: "DELETE")
    public static let patch = HttpVerb(rawValue: "PATCH")
}
