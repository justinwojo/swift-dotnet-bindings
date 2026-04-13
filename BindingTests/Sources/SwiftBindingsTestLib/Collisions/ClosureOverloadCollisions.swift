// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Method Overload Collision Disambiguation
// Pattern: Multiple Swift overloads that project to the same C# signature.
// Instead of skipping all but the first, the generator disambiguates with
// numeric suffixes (e.g., Process, Process2).

/// Class with Array/Set overloads that collide in C# (both project to IEnumerable<string>).
/// The generator must disambiguate the second overload with a numeric suffix.
public class CollectionProcessor {
    public init() {}

    /// Process an array of items. Projects to IEnumerable<string> in C#.
    public func process(items: [String]) -> String {
        return "array:\(items.joined(separator: ","))"
    }

    /// Process a set of items. Also projects to IEnumerable<string> in C#.
    /// The generator should disambiguate this as Process2.
    public func process(unique items: Set<String>) -> String {
        return "set:\(items.sorted().joined(separator: ","))"
    }
}

/// Free functions with the same collision pattern (tests ModuleHandler path).
public func transformCollection(items: [String]) -> String {
    return "array:\(items.joined(separator: ","))"
}

public func transformCollection(unique items: Set<String>) -> String {
    return "set:\(items.sorted().joined(separator: ","))"
}
