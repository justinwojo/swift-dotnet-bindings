// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the property-drop bug: plain stored properties on a
// PAT-constrained generic parent were silently dropped from the generated
// binding with no tombstone comment. The accessor preflight in PropertyHandler
// blocked emission when the parent's generic parameter carried a constraint on
// a protocol with associated types (PAT) — even though the accessor body
// merely reads/writes a stored offset and never dispatches through the PAT
// witness table.
//
// `Bag<Item: BagItem>` mirrors the MusicKit `MusicLibraryRequest<T>` shape:
// a generic struct with a PAT-rooted generic parameter and three plain
// properties (`limit`, `offset`, `includeArchived`) whose types do not
// reference the associated type. These should emit and round-trip per
// closed conformer.
//
// `selectedFilter: Item.Filter` is the negative case: its type references the
// parent's associated type and cannot resolve at the open-generic emission
// site. It must remain suppressed AND emit a visible `// Unsupported:`
// tombstone in the generated `.cs` so consumers can see why it was dropped.

import Foundation

public protocol BagItem {
    associatedtype Filter
    associatedtype SortKey
}

public struct PlainStringItem: BagItem {
    public typealias Filter = Bool
    public typealias SortKey = Int32
    public init() {}
}

public struct PlainIntItem: BagItem {
    public typealias Filter = Bool
    public typealias SortKey = Int32
    public init() {}
}

public struct Bag<Item: BagItem> {
    public var limit: Int = 25
    public var offset: Int = 0
    public var includeArchived: Bool? = nil

    /// Negative case — its type references the parent's associated type
    /// (`Item.Filter`). Resolving this at the open-generic emission site would
    /// require dispatching through `BagItem`'s associated-type witness table.
    /// The property must remain suppressed AND surface a visible `// Unsupported:`
    /// tombstone in the generated `.cs` so the omission is no longer silent.
    public var selectedFilter: Item.Filter? = nil

    public init() {}
}

// MARK: - Closed-conformer factories
//
// The properties above emit per closed conformer through CSM. To keep the
// runtime test path independent of any CSM-specific wrapper plumbing for
// constructors, expose typed factories that return the closed instantiations
// the tests exercise.

public func makeBagPlainStringItem() -> Bag<PlainStringItem> {
    return Bag<PlainStringItem>()
}

public func makeBagPlainIntItem() -> Bag<PlainIntItem> {
    return Bag<PlainIntItem>()
}
