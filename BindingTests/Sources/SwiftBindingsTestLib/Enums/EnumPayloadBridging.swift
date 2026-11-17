// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Payloads of Objective-C-bridgeable Swift values

private nonisolated(unsafe) weak var bridgedPayloadStorage: AnyObject?

/// Records a weak reference to a collection's native storage object. Arrays, dictionaries and sets
/// of these element types are always native Swift storage, a single reference in the payload.
private func observeStorage<T>(_ collection: T) {
    bridgedPayloadStorage = withUnsafeBytes(of: collection) {
        Unmanaged<AnyObject>.fromOpaque($0.load(as: UnsafeRawPointer.self)).takeUnretainedValue()
    }
}

/// Whether the native storage of the collection last built by `BridgedValuePayload`'s factories
/// is still alive. Once the enum value holding it is gone, a reference the binding failed to
/// release keeps it alive.
public func isBridgedPayloadStorageAlive() -> Bool { bridgedPayloadStorage != nil }

/// An enum whose payloads are Objective-C-bridgeable Swift values and collections of them. The
/// payload sits in the enum as Swift values, not as the Objective-C objects they bridge to, so
/// reading it back has to go through Swift's bridge.
public enum BridgedValuePayload {
    case link(URL)
    case maybeLink(URL?)
    case path(IndexPath)
    case amount(Decimal)
    case links([URL])
    case maybeLinks([URL]?)
    case linkMap([String: URL])
    case linkSet(Set<URL>)
    case paths([IndexPath])
    /// A multi-value payload with a bridged collection: TryGet is refused, the factory still binds.
    case labelledLinks(links: [URL], count: Int32)
    /// Bridgeable values nested deeper than the payload bridge reaches; TryGet is refused.
    case nestedLink(URL??)
    case nestedLinks([URL]??)
    case none

    /// `.links` over `count` URLs, built in Swift so its storage can be observed.
    public static func makeLinks(_ count: Int32) -> BridgedValuePayload {
        let links = (0..<count).map { URL(string: "https://bridged.example/\($0)")! }
        observeStorage(links)
        return .links(links)
    }

    /// `.linkMap` over `count` entries keyed "k0", "k1", …
    public static func makeLinkMap(_ count: Int32) -> BridgedValuePayload {
        var map: [String: URL] = [:]
        for i in 0..<count { map["k\(i)"] = URL(string: "https://bridged.example/map/\(i)")! }
        observeStorage(map)
        return .linkMap(map)
    }

    /// `.linkSet` over `count` URLs.
    public static func makeLinkSet(_ count: Int32) -> BridgedValuePayload {
        let set = Set((0..<count).map { URL(string: "https://bridged.example/set/\($0)")! })
        observeStorage(set)
        return .linkSet(set)
    }

    /// `.paths` over `count` one-index paths 0, 1, 2, …
    public static func makePaths(_ count: Int32) -> BridgedValuePayload {
        let paths = (0..<count).map { IndexPath(index: Int($0)) }
        observeStorage(paths)
        return .paths(paths)
    }

    public static func makeAmount(_ text: String) -> BridgedValuePayload {
        .amount(Decimal(string: text)!)
    }
}

// MARK: - Existential payloads with no protocol record

private nonisolated(unsafe) var describedTokenLive: Int32 = 0

/// How many `DescribedToken` instances are alive.
public func getDescribedTokenLiveCount() -> Int32 { describedTokenLive }

/// A class conforming only to standard-library protocols the binding has no record for.
public final class DescribedToken: CustomStringConvertible {
    public let id: Int32
    public init(id: Int32) {
        self.id = id
        describedTokenLive += 1
    }
    deinit { describedTokenLive -= 1 }
    public var description: String { "token\(id)" }
}

/// An enum whose payload is an existential of a protocol the binding has no record for. No proxy
/// can own such a value, so TryGet is refused; the cases still construct and dispose.
public enum UnrecordedExistentialPayload {
    case described(any CustomStringConvertible)
    case none

    public static func makeDescribed(_ id: Int32) -> UnrecordedExistentialPayload {
        .described(DescribedToken(id: id))
    }

    /// The payload's description, read in Swift.
    public var payloadDescription: String {
        if case .described(let value) = self { return value.description }
        return "<none>"
    }
}
