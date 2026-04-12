// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - SimpleEnumLocalizedError (fix #4 runtime path)
//
// A true simple enum — every case is payloadless — conforming to LocalizedError.
// Pinning the simple-enum codepath: even one associated-value case would
// reclassify the enum out of the simple-enum emission branch and miss fix #4
// entirely. The whole point of this fixture is the payloadless shape.

/// Demo enum whose cases conform to Error + LocalizedError without any
/// associated values. Fix #4 (commit 4235d568) wraps the emitted C#
/// extension on this enum's Optional<String> errorDescription in a proper
/// LocalizedError projection that round-trips across throw/catch.
public enum DemoLocalizedError: Error {
    case missing
    case truncated
}

extension DemoLocalizedError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .missing:
            return "Demo: missing"
        case .truncated:
            return "Demo: truncated"
        }
    }
}

/// Throws DemoLocalizedError.missing — used by the C# side to exercise the
/// throw/catch runtime path on a simple-enum LocalizedError. A dedicated
/// free function (rather than a method on the enum) keeps the emission
/// shape as close to real consumer code as possible.
public func throwDemoMissing() throws {
    throw DemoLocalizedError.missing
}

/// Throws DemoLocalizedError.truncated. Companion to throwDemoMissing so
/// the C# test can verify both cases round-trip with distinct descriptions.
public func throwDemoTruncated() throws {
    throw DemoLocalizedError.truncated
}
