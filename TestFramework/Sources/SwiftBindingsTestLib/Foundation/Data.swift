// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Data as Parameter

/// Returns the length of the given Data.
public func dataLength(_ data: Data) -> Int32 {
    return Int32(data.count)
}

// MARK: - Data as Return

/// Creates a Data from the UTF-8 encoding of the given string.
public func makeData(from string: String) -> Data {
    return Data(string.utf8)
}

// MARK: - Data Round-Trip

/// Appends suffix to data and returns the combined result.
public func appendData(_ data: Data, with suffix: Data) -> Data {
    var result = data
    result.append(suffix)
    return result
}

// MARK: - Struct with Data Property

/// Container holding a Data payload.
public struct DataContainer {
    public var payload: Data

    public init(payload: Data) {
        self.payload = payload
    }

    /// Number of bytes in the payload.
    public var byteCount: Int32 {
        return Int32(payload.count)
    }

    /// Returns the first n bytes of the payload, or all bytes if n exceeds length.
    public func prefix(_ n: Int32) -> Data {
        return payload.prefix(Int(n))
    }
}

// MARK: - Optional Data

/// Creates Data from an optional string. Returns nil if the input is nil.
public func optionalData(from string: String?) -> Data? {
    guard let string = string else { return nil }
    return Data(string.utf8)
}
