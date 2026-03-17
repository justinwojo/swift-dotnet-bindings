// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Extension on Data

extension Data {
    /// Returns the hex string representation of the data.
    public var hexString: String {
        return map { String(format: "%02x", $0) }.joined()
    }

    /// Returns the data interpreted as a UTF-8 string, or nil if invalid.
    public func utf8String() -> String? {
        return String(data: self, encoding: .utf8)
    }
}

// MARK: - Extension on URL

extension URL {
    /// Returns true if the URL uses the HTTPS scheme.
    public var isSecure: Bool {
        return scheme == "https"
    }
}

// MARK: - Protocol for Retroactive Conformance

/// A protocol describing types that have a byte size.
public protocol Sizeable {
    var byteSize: Int32 { get }
}

// MARK: - Retroactive Conformance

extension Data: Sizeable {
    public var byteSize: Int32 {
        return Int32(count)
    }
}

// MARK: - Free Function Using Extensions

/// Describes the data using extension methods: hex string and byte count.
public func describeData(_ data: Data) -> String {
    return "Data(\(data.byteSize) bytes): \(data.hexString)"
}
