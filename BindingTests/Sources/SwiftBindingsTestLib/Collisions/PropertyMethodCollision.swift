// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Property-vs-method name collision on a plain type
//
// C# forbids a property and a method sharing a name on the same type (CS0102). When a Swift type
// has a stored property `conflict` AND a method `conflict(_:)`, the authoritative emitted method
// name folds in a property-collision RENAME (`Conflict` → `ConflictMethod`). If a sibling method is
// already named `conflictMethod(_:)`, that rename lands on the sibling's own natural name, so the
// sibling has to move: it carries no argument label, so it escalates to the type rung and emits
// `ConflictMethodWithInt32`. This is the residual case the type-body-wide name map cannot see on
// its own — the map is property-agnostic by design (so that the emitter and the conformance
// validator agree by construction), so the escalation happens in the emission loop, where the
// already-claimed name is visible. The dedup keys and the same-module override verifier must
// observe BOTH the property rename AND that escalation, or the two methods emit under the same C#
// name (CS0111) and dispatch binds to the wrong slot. Each body returns a distinct value so a
// wrong-slot binding is caught.

public class PropertyMethodCollider {
    public var conflict: Int32
    public init(conflict: Int32) { self.conflict = conflict }

    /// Collides with the stored property `conflict` → renamed away from `Conflict`.
    public func conflict(_ x: Int32) -> Int32 { return conflict + x }

    /// Already spelled like the rename target → forced off its natural name to the type rung.
    public func conflictMethod(_ x: Int32) -> Int32 { return conflict * 10 + x }
}

/// CONTROL: same method names, NO colliding property. Without the property the method keeps its
/// natural name (`Conflict` / `ConflictMethod`), so neither the rename nor the escalation runs —
/// pins that both are driven by the sibling property, not by the method names alone.
public class PropertyMethodControl {
    public init() {}
    public func conflict(_ x: Int32) -> Int32 { return x + 1 }
    public func conflictMethod(_ x: Int32) -> Int32 { return x + 2 }
}
