// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Combine

// MARK: - Generic classes whose parameter is class-bound, reached through direct transitions
//
// A class generic over a parameter constrained to a protocol with an associated type
// (`HolderAlertKind` refines `Identifiable`) has no @_cdecl wrapper the generator can specialize, so
// each member is a direct Swift-convention import. Its other parameter is class-bound, and Swift
// passes and returns a class-bound generic value as one object reference, `V?` included, not by
// address. Two shapes make a parameter class-bound:
//
// - `HolderRevisioned: AnyObject`, which the module's own declarations record.
// - `HolderObservedRevisioned: ObservableObject`, class-bound only through Combine's
//   `ObservableObject: AnyObject`, which no declaration of this module states.
//
// The imports once took such a value by the address of a buffer holding it, and returned it
// through the indirect-result register, so the lowering evidence refused their bridges.

/// A class-bound protocol with a mutable revision.
public protocol HolderRevisioned: AnyObject {
    var revision: Int { get set }
}

/// Class-bound only through Combine's `ObservableObject`.
public protocol HolderObservedRevisioned: ObservableObject {
    var revision: Int { get set }
}

/// A protocol with an associated type, which keeps the holders below off the @_cdecl wrapper.
public protocol HolderAlertKind: Identifiable {
    var title: String { get }
}

/// The fixture's conformer to both class-bound protocols.
public final class HolderRevision: HolderRevisioned, HolderObservedRevisioned {
    public var revision: Int

    public init(revision: Int) { self.revision = revision }
}

/// The fixture's conformer to the alert protocol.
public struct HolderAlert: HolderAlertKind {
    public let id: Int
    public let title: String

    public init(id: Int, title: String) {
        self.id = id
        self.title = title
    }
}

/// Holds a class-bound model: every member takes or returns a `V` or `V?` as one reference.
public final class ClassBoundModelHolder<V: HolderRevisioned, A: HolderAlertKind> {
    public let model: V
    public var current: V

    public init(model: V) {
        self.model = model
        self.current = model
    }

    /// Makes `other` current and returns the previous current value.
    public func exchange(_ other: V) -> V {
        let old = current
        current = other
        return old
    }

    /// `other` when present, otherwise nil; makes a present `other` current.
    public func optionalExchange(_ other: V?) -> V? {
        if let other { current = other }
        return other
    }

    /// The current value when its revision is at least `revision`, otherwise nil.
    public func current(atLeast revision: Int) -> V? {
        current.revision >= revision ? current : nil
    }

    /// Swaps `value` with the current value.
    public func swapIn(_ value: inout V) {
        let old = current
        current = value
        value = old
    }

    /// The revision of `value` plus the current value's revision.
    public func revisionSum(_ value: V) -> Int { value.revision + current.revision }

    /// Returns `value` unchanged: a method-level class-bound parameter.
    public func pick<W: HolderRevisioned>(_ value: W) -> W { value }

    /// Returns `value` unchanged: a static member, whose self is the metatype.
    public static func echo(_ value: V) -> V { value }
}

/// Holds a model whose class-boundness comes from Combine's `ObservableObject`.
public final class ObservedModelHolder<V: HolderObservedRevisioned, A: HolderAlertKind> {
    public let model: V
    public var current: V

    public init(model: V) {
        self.model = model
        self.current = model
    }

    /// Makes `other` current and returns the previous current value.
    public func exchange(_ other: V) -> V {
        let old = current
        current = other
        return old
    }

    /// The revision of `value` read in Swift.
    public func revisionOf(_ value: V) -> Int { value.revision }
}

/// The revision of a holder's current value, read in Swift.
public func classBoundHolderCurrentRevision(_ holder: ClassBoundModelHolder<HolderRevision, HolderAlert>) -> Int {
    holder.current.revision
}
