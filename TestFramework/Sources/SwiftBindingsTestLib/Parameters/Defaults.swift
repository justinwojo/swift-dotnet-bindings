// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Default Parameters (Tier 2)

/// Function with a default parameter.
public func greet(name: String, greeting: String = "Hello") -> String {
    return "\(greeting), \(name)!"
}

/// Function with multiple default parameters.
public func search(query: String, limit: Int32 = 10, offset: Int32 = 0) -> String {
    return "Search '\(query)' limit=\(limit) offset=\(offset)"
}

/// Function with mixed required and default parameters.
public func configure(host: String, port: Int32 = 8080, secure: Bool = true) -> String {
    let scheme = secure ? "https" : "http"
    return "\(scheme)://\(host):\(port)"
}
