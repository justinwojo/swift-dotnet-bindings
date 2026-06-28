// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - `any Sendable` in dictionary value position
//
// Mirrors Nuke's `ImageContainer.userInfo` / `ImageRequest.userInfo`, typed
// `[UserInfoKey: any Sendable]`. `any Sendable` is a marker-protocol existential:
// after the generator filters marker protocols it has zero effective protocols,
// so the value position projects to `object` (the same surface as bare `Any`),
// NOT a dropped member. Both a String-keyed form (isolates the existential value)
// and a custom-Hashable-struct-keyed form (faithful to Nuke's UserInfoKey) are
// exercised so a key-marshalling failure can be told apart from a value-projection
// failure.

/// Custom Hashable key, like Nuke's `UserInfoKey` struct.
public struct SendableInfoKey: Hashable {
    public let raw: String
    public init(_ raw: String) { self.raw = raw }
}

/// Carries a `[String: any Sendable]` and a `[SendableInfoKey: any Sendable]`.
public final class SendableInfoBox {
    public var stringKeyed: [String: any Sendable]
    public var structKeyed: [SendableInfoKey: any Sendable]

    public init(stringKeyed: [String: any Sendable]) {
        self.stringKeyed = stringKeyed
        self.structKeyed = [:]
    }

    public func stringKeyedCount() -> Int { stringKeyed.count }
    public func structKeyedCount() -> Int { structKeyed.count }

    /// Reads a String value out of the `any Sendable` dictionary by key.
    public func stringValue(_ key: String) -> String {
        return (stringKeyed[key] as? String) ?? ""
    }

    /// Reads an Int value out of the `any Sendable` dictionary by key.
    public func intValue(_ key: String) -> Int {
        return (stringKeyed[key] as? Int) ?? -1
    }

    public func setStructKeyed(_ value: [SendableInfoKey: any Sendable]) {
        self.structKeyed = value
    }

    public func structKeyedStringValue(_ key: String) -> String {
        return (structKeyed[SendableInfoKey(key)] as? String) ?? ""
    }
}

/// Free function counting a `[String: any Sendable]` (param direction).
public func countSendableInfo(_ info: [String: any Sendable]) -> Int {
    return info.count
}

/// Free function returning a `[String: any Sendable]` (return direction).
public func makeSendableInfo(name: String, count: Int) -> [String: any Sendable] {
    return ["name": name, "count": count]
}

// MARK: - `Result<T, any Error>` in return position
//
// Mirrors Lottie's `DotLottieFile.SynchronouslyBlockingCurrentThread.loadedFrom` /
// `.named`, typed `-> Result<DotLottieFile, any Error>`. `any Error` is the
// well-known stdlib error existential, projected to `Swift.Foundation.AnyError`.
// The success payload is a bound class.

/// Error type returned in the failure arm.
public enum AssetLoadError: Error {
    case notFound
    case corrupted
}

/// Bound class used as the success payload.
public final class LoadedAsset {
    public let name: String
    public init(name: String) { self.name = name }
}

/// Namespace exposing the blocking `Result<T, any Error>` loaders.
public enum AssetLoader {
    /// Returns `.success` for a non-empty path, `.failure(AssetLoadError.notFound)` otherwise.
    public static func loadedFrom(path: String) -> Result<LoadedAsset, any Error> {
        if path.isEmpty {
            return .failure(AssetLoadError.notFound)
        }
        return .success(LoadedAsset(name: path))
    }

    /// Returns `.success` for a non-empty name, `.failure(AssetLoadError.corrupted)` otherwise.
    public static func named(_ name: String) -> Result<LoadedAsset, any Error> {
        if name.isEmpty {
            return .failure(AssetLoadError.corrupted)
        }
        return .success(LoadedAsset(name: name))
    }

    // `Result<T, any Error>` in PARAMETER position is structurally unsupported: SwiftResult
    // has no outbound payload-synthesis path (ResultProjection.GetParameterPlan throws), so the
    // generator must gracefully DROP a member that takes a Result argument — never emit a wrapper
    // that crashes generation. These two members exist solely as the negative gate: a successful
    // compile-only run proves the Result-parameter member was dropped (not emitted broken), with
    // the sibling return-position loaders above proving Result-in-return still projects. No C#
    // round-trip test references them.

    /// Free function taking a `Result<String, any Error>` parameter — must be gracefully dropped.
    public static func describe(_ value: Result<String, any Error>) -> String {
        switch value {
        case .success(let s): return "ok:\(s)"
        case .failure: return "err"
        }
    }
}

/// Instance method taking a `Result<LoadedAsset, any Error>` parameter — must be gracefully dropped.
public final class ResultParameterConsumer {
    public init() {}

    public func consume(_ value: Result<LoadedAsset, any Error>) -> String {
        switch value {
        case .success(let a): return a.name
        case .failure: return ""
        }
    }
}

// MARK: - `Result<T, any Error>` in WRITE-IN (setter / subscript-input) position
//
// Result is supported ONLY in the read/return direction (the loaders above). A Result that
// reaches a property SETTER value, a SUBSCRIPT index, or a settable-subscript newValue is the
// outbound/parameter direction, which SwiftResult cannot synthesize — so the member must be
// gracefully DROPPED, never emitted with a wrapper that mis-marshals a C#-constructed Result.
// These exercise the ACCESSOR paths (property setter, subscript index, subscript setter newValue)
// which are validated separately from method parameters and would otherwise slip past the
// parameter-direction Result gate. A successful compile-only run is the gate (no C# round-trip
// test references them); the read-only Result siblings above prove return-position still projects.

/// Settable `Result<String, any Error>` property — its SETTER takes Result (parameter direction),
/// so the whole property must be dropped (the read-only loaders above stay supported).
public final class SettableResultBox {
    public var value: Result<String, any Error>
    public init() { self.value = .success("init") }
}

/// Subscript whose INDEX is a `Result<String, any Error>` (parameter direction) — must be dropped.
public final class ResultIndexedRegistry {
    private var store: [String: String] = [:]
    public init() {}

    public subscript(key: Result<String, any Error>) -> String {
        switch key {
        case .success(let s): return store[s] ?? ""
        case .failure: return ""
        }
    }
}

/// Settable subscript RETURNING `Result<String, any Error>` — the setter's newValue is Result
/// (parameter direction), so the subscript must be dropped (no read-only getter survives either).
public final class SettableResultSubscriptBox {
    private var slots: [String]
    public init() { self.slots = ["a", "b", "c"] }

    public subscript(i: Int) -> Result<String, any Error> {
        get {
            guard i >= 0 && i < slots.count else { return .failure(AssetLoadError.notFound) }
            return .success(slots[i])
        }
        set {
            if case .success(let s) = newValue, i >= 0, i < slots.count {
                slots[i] = s
            }
        }
    }
}

// MARK: - `[any P.Type]` metatype array in return / property position (DEFERRED)
//
// Mirrors MusicKit's `MusicCatalogSearchRequest.types` (and the 4 sibling
// request `.types` properties), typed `[any SearchableItem.Type]`. The PARAMETER
// direction is supported (joinSearchableKinds, bespoke metadata-handle buffer
// marshalling). The RETURN direction below is a DIFFERENT, unimplemented mechanism:
// materialising a Swift `[any P.Type]` back into C# has no canonical surface (a
// Swift metatype is not a System.Type), so the generator gracefully DROPS these
// members rather than emitting broken code. This fixture intentionally stays in the
// dropped state as the documented limitation gate — no round-trip test asserts it.
// SearchableItem / SongItem / AlbumItem / ArtistItem are declared in
// Generics/Metatypes.swift and registered as known conformers.

/// Holds a mutable `[any SearchableItem.Type]` filter, like a MusicKit request.
public final class SearchableTypeRegistry {
    private var registered: [any SearchableItem.Type]

    public init() {
        self.registered = [SongItem.self, AlbumItem.self, ArtistItem.self]
    }

    /// Property getter returning the metatype array (read-back position).
    public var types: [any SearchableItem.Type] {
        return registered
    }

    /// Method returning the metatype array.
    public func allTypes() -> [any SearchableItem.Type] {
        return registered
    }

    /// Joined itemKind over the stored types (oracle for the round-trip).
    public func joinedKinds() -> String {
        return registered.map { $0.itemKind }.joined(separator: ",")
    }
}

// MARK: - `[any P]` plain-protocol array in property / return position
//
// Mirrors RoomPlan's `CapturedRoom.Object.attributes`, typed
// `[any CapturedRoomAttribute]`. Parameter direction is already covered
// (describeAll); this exercises the uncovered property getter + method return.
// Describable / SimpleItem are declared in Generics/ (Describable.swift).

/// Holds `[any Describable]` and exposes it via a property getter and a method.
public final class DescribableBag {
    private let items: [any Describable]

    public init() {
        self.items = [
            SimpleItem(id: "a", label: "alpha"),
            SimpleItem(id: "b", label: "beta"),
            SimpleItem(id: "c", label: "gamma"),
        ]
    }

    /// Property getter returning the existential array (read-back position).
    public var contents: [any Describable] {
        return items
    }

    /// Method returning the existential array.
    public func allItems() -> [any Describable] {
        return items
    }

    /// Joined describe() over the stored items (oracle for the round-trip).
    public func joinedDescriptions() -> String {
        return items.map { $0.describe() }.joined(separator: ",")
    }
}
