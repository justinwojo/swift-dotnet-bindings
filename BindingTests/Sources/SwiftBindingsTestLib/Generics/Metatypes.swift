// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Metatype Parameters

/// Returns the type name of the given metatype.
/// Tests: T.Type parameter, T.self usage.
/// Expected C#: TypeMetadata or IntPtr representing the Swift metatype.
public func typeName<T>(of type: T.Type) -> String {
    return String(describing: type)
}

/// Creates a default instance of a type that conforms to DefaultInitializable.
public protocol DefaultInitializable {
    init()
}

/// Creates an instance from a metatype parameter.
public func createInstance<T: DefaultInitializable>(of type: T.Type) -> T {
    return T()
}

// MARK: - Concrete Types for Metatype Tests

/// A simple struct conforming to DefaultInitializable.
@frozen
public struct MetatypeTestStruct: DefaultInitializable {
    public var value: Int32

    public init() {
        self.value = 0
    }

    public init(value: Int32) {
        self.value = value
    }
}

/// Another type for metatype comparison tests.
@frozen
public struct AnotherMetatypeStruct: DefaultInitializable {
    public var tag: Int32

    public init() {
        self.tag = -1
    }

    public init(tag: Int32) {
        self.tag = tag
    }
}

// MARK: - Metatype Return

/// Returns the metatype of the given value.
public func getType<T>(of value: T) -> T.Type {
    return type(of: value)
}

// MARK: - Metatype Comparison

/// Checks whether two values have the same type.
public func isSameType<T, U>(_ a: T, _ b: U) -> Bool {
    return type(of: a as Any) == type(of: b as Any)
}

// MARK: - Struct with Metatype Method

/// A class that stores a type and uses it for instance creation.
public class TypeFactory<T: DefaultInitializable> {
    public let storedType: T.Type

    public init(type: T.Type) {
        self.storedType = type
    }

    /// Creates a new instance of the stored type.
    public func create() -> T {
        return storedType.init()
    }
}

// MARK: - Existential Metatype Arrays

/// Protocol with known conformers registered in specialization-hints.json.
/// Used to test `[any SearchableItem.Type]` parameter marshalling.
public protocol SearchableItem {
    static var itemKind: String { get }
}

public struct SongItem: SearchableItem {
    public static var itemKind: String { return "song" }
    public init() {}
}

public struct AlbumItem: SearchableItem {
    public static var itemKind: String { return "album" }
    public init() {}
}

public struct ArtistItem: SearchableItem {
    public static var itemKind: String { return "artist" }
    public init() {}
}

/// Narrower protocol used solely by the payload-oracle fixture. Kept distinct from
/// SearchableItem so adding the payload-bearing conformer does NOT ripple into the
/// GenericContainer<T: SearchableItem> and ElementBoundContainer<T: SearchableItem>
/// CSM matrices. Each registered protocol drives its own conformer-pairing emission
/// in CSM, so the only specialization ValidatableItem produces is on the dedicated
/// ThrowingItemNamespace.validateAndReturnTagged method below. Named distinctly from
/// the existing `TaggedItem` struct in Protocols/Conformance.swift to avoid collision.
public protocol ValidatableItem {
    static var itemKind: String { get }
}

/// Payload-bearing ValidatableItem conformer used as the value-oracle for the
/// generic-parameter return shape under throws. SongItem/AlbumItem/ArtistItem are
/// empty marker structs — a success test on validateAndReturn<T: SearchableItem>
/// that uses only them can only assert the runtime type survives, not that the
/// `resultPtr` actually carried the input payload across the @_cdecl boundary.
/// A struct with a stored `id` field lets the test pin payload round-trip,
/// catching a hypothetical wrapper bug where the catch arm clears the buffer or
/// the success arm returns a fresh `TaggedSearchItem()` rather than the input.
public struct TaggedSearchItem: ValidatableItem {
    public static var itemKind: String { return "tagged" }
    public let id: UInt32
    public init(id: UInt32) {
        self.id = id
    }
}

/// Takes `[any SearchableItem.Type]` and returns the joined itemKind of each type.
/// Tests existential-metatype array parameter marshalling (Fix 5).
public func joinSearchableKinds(_ types: [any SearchableItem.Type]) -> String {
    return types.map { $0.itemKind }.joined(separator: ",")
}

/// Returns the count of existential metatypes passed in.
public func countSearchableTypes(_ types: [any SearchableItem.Type]) -> Int {
    return types.count
}
