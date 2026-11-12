// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @autoclosure on closure-bridged methods

/// `choose`'s `ProcessResult` handler routes it through the method-closure bridge, which adapts
/// each managed callback into a Swift closure. The function-taking sibling takes any call that
/// hands `value` that closure rather than its Bool, and answers -1, so a call bound to it shows.
public final class AutoclosureBridgeHost {
    public init() {}

    /// Evaluates `value` only when `enabled`, reports it to `handler` as success or failure
    /// carrying `code`, and returns 1 or 0 for it; returns 2 when disabled.
    public func choose(
        enabled: Bool,
        value: @autoclosure () -> Bool,
        handler: (ProcessResult) -> Void,
        code: [Int32] = [7]
    ) -> Int32 {
        guard enabled else { return 2 }
        let outcome = value()
        handler(outcome ? .success(value: code[0]) : .failure(code: code[0]))
        return outcome ? 1 : 0
    }

    public func choose(enabled: Bool, value: () -> Any, handler: (ProcessResult) -> Void) -> Int32 {
        -1
    }

    /// The same shape with a handler whose argument, a generic class, lowers directly: the bridge
    /// then writes each closure as a literal in the call instead of binding an adapter. Returns
    /// `code` when `value` holds and 0 when it does not, after handing `handler` a box naming it.
    public func pick(
        value: @autoclosure () -> Bool,
        handler: (AutoclosureBox<DatabaseReader>) -> Void,
        code: [Int32] = [7]
    ) -> Int32 {
        let outcome = value()
        handler(AutoclosureBox(item: DatabaseReader(name: outcome ? "held" : "failed")))
        return outcome ? code[0] : 0
    }

    public func pick(value: () -> Any, handler: (AutoclosureBox<DatabaseReader>) -> Void) -> Int32 {
        -1
    }

    /// Returns `value`. The throwing generic autoclosure routes it through the generic closure
    /// bridge, which hands the call a closure literal of its own.
    public func evaluate<T>(_ value: @autoclosure () throws -> T) rethrows -> T {
        try value()
    }
}

/// A generic class for `pick`'s handler to receive.
public final class AutoclosureBox<Item> {
    public let item: Item

    public init(item: Item) {
        self.item = item
    }
}

// MARK: - @autoclosure on a protocol-extension default

/// The protocol-extension bridge binds the managed callback to a closure of its own and passes it
/// to `admit`. The function-taking sibling records -1, so a call bound to it shows.
public protocol AutoclosureGate: AnyObject {
    var admitted: Int32 { get set }
}

extension AutoclosureGate {
    /// Records 1 or 0 for `condition`.
    public func admit(_ condition: @autoclosure () -> Bool) {
        admitted = condition() ? 1 : 0
    }

    public func admit(_ condition: () -> Any) {
        admitted = -1
    }
}

public final class AutoclosureGateHost: AutoclosureGate {
    public var admitted: Int32 = 99

    public init() {}
}
