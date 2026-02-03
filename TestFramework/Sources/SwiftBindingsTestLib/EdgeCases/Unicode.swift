// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unicode Edge Cases

/// Struct with a unicode name to test name mangling.
public struct Café {
    public let name: String
    public let rating: Int32

    public init(name: String, rating: Int32) {
        self.name = name
        self.rating = rating
    }

    public func menuItem() -> String {
        return "\(name) (★\(rating))"
    }
}

/// Function with unicode in parameter names.
public func greetCafé(_ café: Café) -> String {
    return "Welcome to \(café.name)!"
}

/// Function returning a Café.
public func makeCafé(name: String) -> Café {
    return Café(name: name, rating: 5)
}
