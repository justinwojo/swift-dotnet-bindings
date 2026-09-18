// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Zero-witness existential parameters on @_cdecl-wrapped methods
//
// A parameter typed as bare `Any` or as a marker-only composition (`any Sendable`) carries no
// witness table: its container is the four-word ExistentialContainer0, and its public C# type is
// `object`. Every method of an NSObject subclass routes through an @_cdecl wrapper, which takes
// the existential by pointer and copies it out with `load(as:)`. The caller therefore has to box
// the C# value into a fresh container it owns, and destroy that container once the call returns.
//
// The wrapper used to cast the `object` argument to ISwiftExistentialConvertible — an interface a
// plain C# string or number never implements — so every call threw InvalidCastException before
// reaching Swift. Each method below describes what arrived, so the C# side can check that the
// value Swift saw is the value it passed.

/// Formats a value as `<dynamic type>:<value>` so a boxing mistake shows up as the wrong type.
private func zeroWitnessDescribe(_ value: Any) -> String {
    "\(type(of: value)):\(value)"
}

/// Value type with no special layout, boxed through its own value witness table.
public struct ZeroWitnessParamPoint {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// Class instance whose lifetime is observable from C#: a boxed reference the caller fails to
/// release keeps it alive, and one released twice crashes.
public final class ZeroWitnessParamToken {
    private static var live: Int32 = 0

    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
        Self.live += 1
    }

    deinit {
        Self.live -= 1
    }

    public static var liveCount: Int32 { live }
}

extension ZeroWitnessParamToken: CustomStringConvertible {
    public var description: String { "token#\(identifier)" }
}

public class ZeroWitnessParameterSink: NSObject {
    public private(set) var lastDescription: String = ""

    public override init() {
        super.init()
    }

    /// Bare `Any`, reachable from Objective-C.
    @objc public func acceptAny(_ value: Any) -> String {
        record(value)
    }

    /// Marker-only composition: the same container as `Any`.
    public func acceptSendable(_ value: any Sendable) -> String {
        record(value)
    }

    /// A zero-witness parameter between two ordinary ones.
    public func acceptLabeled(prefix: String, value: Any, count: Int) -> String {
        "\(prefix)|\(record(value))|\(count)"
    }

    /// Records without returning, so the result is only visible through `lastDescription`.
    public func store(_ value: Any) {
        _ = record(value)
    }

    /// The async wrapper hands the container to the continuation, which frees it after resuming.
    public func acceptAnyAsync(_ value: Any) async -> String {
        record(value)
    }

    public func acceptSendableAsync(_ value: any Sendable) async -> String {
        record(value)
    }

    private func record(_ value: Any) -> String {
        let description = zeroWitnessDescribe(value)
        lastDescription = description
        return description
    }
}
