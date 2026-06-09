// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - GenericIndexableCollection
//
// Mirrors MusicKit's `MusicItemCollection<MusicItemType>: Collection` shape:
// a generic struct whose Collection conformance members are declared inside
// a separate `extension` block, with multiple sibling overloads sharing the
// same base Swift name (`index`, `formIndex`) but distinct argument labels
// (`before:`, `after:`, `_:offsetBy:`). The pre-fix wrapper-emit gate
// dropped ALL same-base-name siblings on the floor with a
// `// Generic static dispatch wrapper skipped` comment whenever any one of
// them was marked as an extension method, because the collision check
// compared only the base Swift name instead of the full selector
// (name + arg-label tuple). The selector-aware fix lets each distinct
// overload emit its @_cdecl wrapper.

public protocol IndexableItem {
    var itemTag: String { get }
}

@frozen
public struct IndexableCoin: IndexableItem {
    public let itemTag: String
    public init(itemTag: String) { self.itemTag = itemTag }
}

public struct GenericIndexableCollection<Item: IndexableItem> {
    public let items: [Item]
    public init(items: [Item]) {
        self.items = items
    }
}

// Conformance + sibling overloads live in an EXTENSION block (matching MusicKit's
// surface). The wrapper-emit's same-base-name collision skip used to drop every
// `index` and `formIndex` overload here; with selector-aware collision detection
// each one emits an @_cdecl symbol.
extension GenericIndexableCollection: Collection {
    public var startIndex: Int { 0 }
    public var endIndex: Int { items.count }
    public subscript(position: Int) -> Item { items[position] }

    public func index(after i: Int) -> Int { i + 1 }
    public func index(before i: Int) -> Int { i - 1 }
    public func index(_ i: Int, offsetBy distance: Int) -> Int { i + distance }

    public func formIndex(after i: inout Int) { i = index(after: i) }
    public func formIndex(before i: inout Int) { i = index(before: i) }
}

public func makeGenericIndexableCollection(
    firstTag: String, secondTag: String, thirdTag: String
) -> GenericIndexableCollection<IndexableCoin> {
    return GenericIndexableCollection(items: [
        IndexableCoin(itemTag: firstTag),
        IndexableCoin(itemTag: secondTag),
        IndexableCoin(itemTag: thirdTag),
    ])
}
