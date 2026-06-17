// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential-overload collapse + reverse dispatch
//
// Regression coverage for a real-world break first seen in FirebaseFirestore: a
// reverse-dispatch protocol declares TWO overloads of the SAME method name whose
// parameters are DIFFERENT existentials — here `record(any OverloadCollapseTagPrimary)`
// and `record(any OverloadCollapseTagSecondary)` (Firestore's shape was
// `add(any Expression)` / `add(any Sendable)`).
//
// The generator's three protocol key functions diverge on this shape:
//   • ProtocolSignatureHelper.GetMethodSignatureKey resolves each existential param via
//     the raw type-record lookup, which does not understand an existential's protocol
//     list and falls back to Swift.AnyType — so BOTH overloads collapse to one key and
//     the C# interface emits a SINGLE method (Record(IOverloadCollapseTagPrimary), first
//     by declaration order).
//   • EveryProtocolEmitter.GetMethodKey (vtable layout) keys off the raw Swift type, which
//     is distinct → TWO witness slots are allocated.
//   • GetProjectedCSharpMethodKey routes through the factory, which resolves each
//     registered protocol existential to its DISTINCT interface → the projected-key dedup
//     in the proxy receiver/static-init loops does NOT collapse them.
//
// Before the fix the proxy therefore emitted a SECOND receiver dispatching to a
// non-existent Record(IOverloadCollapseTagSecondary) overload → CS1503 in the generated
// binding (24 of them in FirebaseFirestore.cs). The fix adds a raw-signature dedup to the
// receiver + static-init loops so only the surviving (first) overload's receiver is
// emitted; the collapsed slot is left null, matching the interface's one-method reality.
//
// This fixture's mere successful build is the primary regression guard; the runtime leg
// additionally proves the surviving overload reverse-dispatches into a C# conformer.

public protocol OverloadCollapseTagPrimary {
    var primaryId: Int32 { get }
}

public protocol OverloadCollapseTagSecondary {
    var secondaryId: Int32 { get }
}

/// A concrete primary tag the Swift driver hands to the delegate so the C# conformer
/// receives a non-nil `IOverloadCollapseTagPrimary` it can read through.
public final class OverloadCollapsePrimaryBox: OverloadCollapseTagPrimary {
    public let primaryId: Int32
    public init(primaryId: Int32) { self.primaryId = primaryId }
}

/// Reverse-dispatch delegate carrying the two collapsing `record` overloads.
public protocol OverloadCollapseDelegate: AnyObject {
    func record(_ value: any OverloadCollapseTagPrimary) -> Int32
    func record(_ value: any OverloadCollapseTagSecondary) -> Int32
}

/// Swift driver: holds the delegate weakly and fires the FIRST (surviving) overload.
public final class OverloadCollapseSource {
    public weak var delegate: OverloadCollapseDelegate?

    public init() {}

    /// Routes through the surviving `record(any OverloadCollapseTagPrimary)` overload.
    /// Returns the delegate's result, or -1 when no delegate is set.
    public func firePrimary(id: Int32) -> Int32 {
        guard let d = delegate else { return -1 }
        return d.record(OverloadCollapsePrimaryBox(primaryId: id))
    }
}
