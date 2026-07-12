// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Per-T projection of a generic-container property (MusicItemCollection<T> shape)
//
// Mirrors the MusicKit family where a generic response exposes a property whose
// type is a generic container of the enclosing generic parameter:
//   MusicLibraryResponse<MusicItemType>.items : MusicItemCollection<MusicItemType>
//
// On the OPEN generic shell the property type `TypedBag<Item>` resolves the
// parent parameter `Item` to `Swift.AnyType`, so PropertyHandler skips `items`
// with reason AnyTypeFallback and the property is dead — a consumer holding a
// `LibraryResponse<TcpAlbum>` cannot reach its items at all. The generic shell
// can't carry a runtime-metadata P/Invoke either (the Mono-JIT pathology with
// two type-metadata arguments). The fix projects the property PER CONFORMER:
// for each closed `LibraryResponse<Album>` a closed `Items()` getter returns a
// concretely-typed `TypedBag<Album>` through the parent-CSM extension path.

/// Module-local constraint protocol standing in for a MusicKit item-kind
/// protocol. A plain (non-associated-type) protocol with module-local
/// conformers, so the specialization engine resolves conformers straight from
/// the ABI — no specialization-hints.json entry, exactly like the real
/// module-local analog.
public protocol LibraryItem {
    var itemId: String { get }
}

@frozen
public struct TcpAlbum: LibraryItem {
    public let itemId: String
    public init(itemId: String) { self.itemId = itemId }
}

@frozen
public struct TcpSong: LibraryItem {
    public let itemId: String
    public init(itemId: String) { self.itemId = itemId }
}

/// The `MusicItemCollection<T>` analog: a generic container of the parent's
/// element type. Element identity rides the EXISTING returnsGenericParam
/// method-CSM path (`element(at:) -> Element` → a closed conformer), and
/// `count()` is the concrete control that proves the returned container is
/// real and correctly populated.
public struct TypedBag<Element: LibraryItem> {
    private let storage: [Element]
    public init(_ storage: [Element]) { self.storage = storage }
    public func count() -> Int { storage.count }
    public func element(at index: Int) -> Element { storage[index] }
}

/// The `MusicLibraryResponse<T>` analog. `items` is the property under test:
/// its type `TypedBag<Item>` is a bound generic that MENTIONS the parent
/// parameter `Item`, so it is AnyTypeFallback-skipped on the open shell today
/// and must project per-conformer via the parent-CSM getter extension.
public struct LibraryResponse<Item: LibraryItem> {
    private let bag: TypedBag<Item>
    public init(_ elements: [Item]) { self.bag = TypedBag(elements) }
    public var items: TypedBag<Item> { bag }
}

// MARK: - Closed-conformer factories
//
// Keep the runtime test path independent of any CSM-specific constructor
// plumbing for the generic parent: expose typed factories that return the
// closed instantiations the tests exercise.

public func makeAlbumLibraryResponse() -> LibraryResponse<TcpAlbum> {
    return LibraryResponse([
        TcpAlbum(itemId: "a1"),
        TcpAlbum(itemId: "a2"),
        TcpAlbum(itemId: "a3"),
    ])
}

public func makeSongLibraryResponse() -> LibraryResponse<TcpSong> {
    return LibraryResponse([TcpSong(itemId: "s1")])
}
