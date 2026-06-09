// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Property-vs-method name collision on a plain type
//
// C# forbids a property and a method sharing a name on the same type (CS0102). When a Swift type
// has a stored property `conflict` AND a method `conflict(_:)`, the authoritative emitted method
// name folds in a property-collision RENAME (`Conflict` → `ConflictMethod`). If a sibling method is
// already named `conflictMethod(_:)`, the rename then NUMERICALLY collides (`ConflictMethod` →
// `ConflictMethod2`). The dedup keys and the same-module override verifier must observe BOTH the
// property rename AND the numeric suffix, or the two methods emit under the same C# name (CS0111)
// and dispatch binds to the wrong slot. Each body returns a distinct value so a wrong-slot
// binding is caught.

public class PropertyMethodCollider {
    public var conflict: Int32
    public init(conflict: Int32) { self.conflict = conflict }

    /// Collides with the stored property `conflict` → renamed away from `Conflict`.
    public func conflict(_ x: Int32) -> Int32 { return conflict + x }

    /// Already spelled like the rename target → forces the numeric collision suffix.
    public func conflictMethod(_ x: Int32) -> Int32 { return conflict * 10 + x }
}

/// CONTROL: same method names, NO colliding property. Without the property the method keeps its
/// natural name (`Conflict` / `ConflictMethod`), so the rename never runs — pins that the rename is
/// driven by the sibling property, not by the method names alone.
public class PropertyMethodControl {
    public init() {}
    public func conflict(_ x: Int32) -> Int32 { return x + 1 }
    public func conflictMethod(_ x: Int32) -> Int32 { return x + 2 }
}
