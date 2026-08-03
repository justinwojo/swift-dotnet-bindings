// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collection-overload projection
// Pattern: Swift overloads whose Swift parameter types differ but which a naive projection
// would erase to one C# signature. Distinct projections keep them as ordinary C# overloads
// with no disambiguation at all; where a projection genuinely collides, the overload group is
// renamed from each member's own labels/types (see Collisions/OverloadDeclarationOrderBareName.swift).

/// Class with Array/Set overloads. Both are `process`, but `[String]` projects to
/// `IEnumerable<string>` and `Set<String>` to `IReadOnlySet<string>`, so the projected parameter
/// signatures differ and both keep the natural `Process` name as plain C# overloads.
public class CollectionProcessor {
    public init() {}

    /// Process an array of items. Projects to IEnumerable<string> in C#.
    public func process(items: [String]) -> String {
        return "array:\(items.joined(separator: ","))"
    }

    /// Process a set of items. Projects to IReadOnlySet<string> — a DIFFERENT C# parameter
    /// signature, so no disambiguation is needed and this stays `Process`.
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
