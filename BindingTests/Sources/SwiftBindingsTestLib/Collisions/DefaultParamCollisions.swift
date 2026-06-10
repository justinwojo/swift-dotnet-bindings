// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Default Parameter Overload Collisions
// An explicit overload collides with the default-param-trimmed overload.
// The generator must detect the collision and skip emitting the trimmed
// overload to avoid CS0111.

/// Class with overloads that collide after default parameter trimming.
public class SearchService {
    public init() {}

    /// This has a default param — generator may emit a 1-param overload `find(query:)`.
    public func find(query: String, limit: Int32 = 10) -> String {
        return "find(\(query), limit=\(limit))"
    }

    /// This explicit 1-param overload would collide with the trimmed version above.
    public func find(query: String) -> String {
        return "find(\(query))"
    }
}

/// Free function variant of the same collision pattern.
public func searchItems(query: String, maxResults: Int32 = 20) -> String {
    return "search(\(query), max=\(maxResults))"
}

/// Explicit overload that collides with the trimmed version above.
public func searchItems(query: String) -> String {
    return "search(\(query))"
}
