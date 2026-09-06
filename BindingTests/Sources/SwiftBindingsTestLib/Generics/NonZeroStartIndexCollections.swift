// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collections whose index space does not start at zero
//
// Every other Collection fixture in this library hard-codes `startIndex { 0 }`, so
// the projected `IReadOnlyList<T>` surface can't tell a zero-based offset apart from
// a native Swift index. A Swift `Collection` is under no obligation to start at zero:
// a slice, a window over a larger buffer, or any conformer that carries a base offset
// starts wherever it likes, while `count` stays `endIndex - startIndex`.
//
// `IReadOnlyList<T>` is zero-based by contract — `list[0]` is the first element and
// valid positions are `0 ..< Count`. These fixtures pin that translation: the projection
// must map the managed offset onto the collection's own index space before subscripting,
// and must reject an out-of-range offset as a managed bounds error instead of tripping
// Swift's subscript precondition (a process trap the consumer cannot catch).
//
// Both shapes keep their storage PRIVATE so the collection-witness projection path fires
// (a public `[Element]` property would send them down the array-backed delegation path,
// which reads the array rather than the collection).

/// Window over a private buffer whose index space begins at an arbitrary base.
/// `RandomAccessCollection`, so the stdlib's O(1) index arithmetic is in play.
///
/// A consumer holding this as `IReadOnlyList<Element>` sees `Count` elements at
/// positions `0 ..< Count`, even though Swift code indexing the same value uses
/// `windowBase ..< windowBase + Count`.
public struct OffsetWindow<Element: CollectibleItem>: RandomAccessCollection {
    private let storage: [Element]

    public init(_ storage: [Element]) {
        self.storage = storage
    }

    /// Fixed base for the window's index space. A stored `base` would give the initializer
    /// an `Int` parameter alongside the element array, which is a separate emission shape;
    /// this fixture is about index translation, so it keeps the base out of the signature.
    private var base: Int { 100 }

    /// The first native index of the window — the positive control that this fixture
    /// really is offset, so a zero-based assertion on the projection can't pass vacuously.
    public var windowBase: Int { base }

    // Collection requirements — Index = Int via typealias inference.
    public var startIndex: Int { base }
    public var endIndex: Int { base + storage.count }
    public subscript(position: Int) -> Element {
        precondition(position >= startIndex && position < endIndex, "OffsetWindow index out of range")
        return storage[position - base]
    }
    public func index(after i: Int) -> Int { i + 1 }
    public func index(before i: Int) -> Int { i - 1 }
}

/// Three-element window based at 100 — the ordinary populated case.
public func makeOffsetWindow(firstId: String, secondId: String, thirdId: String) -> OffsetWindow<CollectibleCoin> {
    return OffsetWindow([
        CollectibleCoin(collectibleId: firstId),
        CollectibleCoin(collectibleId: secondId),
        CollectibleCoin(collectibleId: thirdId),
    ])
}

/// Empty window that still starts at 100: `startIndex == endIndex == 100`, `count == 0`.
/// Reading position 0 has to be a managed bounds error, not a native trap — and the old
/// native-index range check would have rejected it for the wrong reason.
public func makeEmptyOffsetWindow() -> OffsetWindow<CollectibleCoin> {
    return OffsetWindow([])
}

/// `ArraySlice`-backed plain `Collection` — no `RandomAccessCollection`, no
/// `BidirectionalCollection`, so only forward traversal is declared. Its index space is
/// inherited from the slice, which starts at the offset the slice was taken from.
public struct SlicedSeries<Element: CollectibleItem>: Collection {
    private let storage: ArraySlice<Element>

    /// Deliberately `internal`: `ArraySlice` is not a bindable parameter type, and a public
    /// initializer taking one would add a skip marker to the module's surface for no reason.
    /// A slice-shaped collection reaches a consumer from a factory anyway, which is how the
    /// runtime tests build one.
    internal init(storage: ArraySlice<Element>) {
        self.storage = storage
    }

    /// The slice's first native index — positive control for a nonzero start.
    public var sliceStart: Int { storage.startIndex }

    // Collection requirements — Index = Int, inherited from the slice.
    public var startIndex: Int { storage.startIndex }
    public var endIndex: Int { storage.endIndex }
    public subscript(position: Int) -> Element { storage[position] }
    public func index(after i: Int) -> Int { i + 1 }
}

/// Drops two leading elements, so the resulting slice starts at native index 2 and
/// holds exactly the three ids passed in.
public func makeSlicedSeries(firstId: String, secondId: String, thirdId: String) -> SlicedSeries<CollectibleCoin> {
    let backing = [
        CollectibleCoin(collectibleId: "dropped-0"),
        CollectibleCoin(collectibleId: "dropped-1"),
        CollectibleCoin(collectibleId: firstId),
        CollectibleCoin(collectibleId: secondId),
        CollectibleCoin(collectibleId: thirdId),
    ]
    return SlicedSeries(storage: backing.dropFirst(2))
}

/// Empty slice taken past the end of a non-empty array: `startIndex == endIndex == 2`,
/// `count == 0`. The shape a consumer reaches after filtering everything out.
public func makeEmptySlicedSeries() -> SlicedSeries<CollectibleCoin> {
    let backing = [
        CollectibleCoin(collectibleId: "dropped-0"),
        CollectibleCoin(collectibleId: "dropped-1"),
    ]
    return SlicedSeries(storage: backing.dropFirst(2))
}
