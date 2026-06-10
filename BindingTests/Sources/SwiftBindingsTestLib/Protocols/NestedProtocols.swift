// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Nested-Protocol Type Reference
//
// Regression coverage for nested-protocol `I`-prefix misplacement:
//
// When a Swift protocol is declared as a nested type (e.g. `Outer.Delegate`),
// the generator must emit references to it as `Outer.IDelegate`, NOT as
// `IOuter.Delegate`. The `I` prefix attaches to the leaf identifier (the
// protocol's own name), not to a path component. The pre-fix generator built
// references via `"I" + qualifiedPath` and produced the nonexistent C# type.

import Foundation

/// Container struct that hosts a nested protocol.
public class NestedProtoOuter {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Nested protocol — a reference like `NestedProtoOuter.Listener` must lower to
    /// `NestedProtoOuter.IListener` in C#, not `INestedProtoOuter.Listener`.
    public protocol Listener {
        func onEvent(value: Int32) -> String
    }

    /// Method that receives the nested protocol as a parameter — exercises the
    /// type-reference emission path that produces the buggy `I{Parent}.{Nested}`.
    public func notify(listener: any NestedProtoOuter.Listener, value: Int32) -> String {
        return listener.onEvent(value: value)
    }
}

/// Free function returning a `NestedProtoOuter` so test code can construct one.
public func makeNestedProtoOuter(label: String) -> NestedProtoOuter {
    return NestedProtoOuter(label: label)
}
