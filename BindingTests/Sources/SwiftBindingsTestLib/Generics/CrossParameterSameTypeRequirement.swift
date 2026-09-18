// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A protocol requirement that pins one generic parameter to another's associated type
//
// `E == S.Element` is a same-type requirement, not a conformance. The parser stores it with the
// other parameter in the target's module slot and the associated type in its name slot, alongside
// the real conformances on the same parameter; the generated EveryProtocol stub then spelled every
// entry after the colon, which produced `<_G0: RosterEntry & Element>` — `Element` names nothing in
// the wrapper's scope, so the whole wrapper failed to compile and the binding was withdrawn.
//
// It also collapses overloads: two requirements differing only in their same-type pin render as one
// signature once the pin is dropped, which is a redeclaration.

public protocol RosterEntry {
    var rosterId: Int { get }
}

public struct StaffEntry: RosterEntry {
    public let rosterId: Int
    public init(rosterId: Int) { self.rosterId = rosterId }
}

public protocol RosterSource {
    /// The shape under test: `E` is pinned to `S.Element` while also carrying a real conformance.
    func firstEntry<E, S>(_ candidates: S) -> E? where E: RosterEntry, E == S.Element, S: Sequence

    /// A sibling that differs from the first only in its same-type pin. Dropping the pins rather
    /// than rendering them makes these two the same signature.
    func firstEntry<E, S>(_ candidates: S, startingAt: Int) -> E?
        where E: RosterEntry, S: Sequence, S.Element == Int
}

public final class StaffRoster: RosterSource {
    private let entries: [StaffEntry]

    public init(ids: [Int]) {
        self.entries = ids.map(StaffEntry.init(rosterId:))
    }

    public var count: Int { entries.count }

    public func firstEntry<E, S>(_ candidates: S) -> E? where E: RosterEntry, E == S.Element, S: Sequence {
        candidates.first { $0.rosterId > 0 }
    }

    public func firstEntry<E, S>(_ candidates: S, startingAt: Int) -> E?
        where E: RosterEntry, S: Sequence, S.Element == Int {
        entries.first { $0.rosterId >= startingAt } as? E
    }

    /// A non-generic member on the same type, so the class still has a callable surface once the
    /// generic requirements above are refused for the reasons they are refused.
    public func idsAbove(_ threshold: Int) -> [Int] {
        entries.map(\.rosterId).filter { $0 > threshold }
    }
}
