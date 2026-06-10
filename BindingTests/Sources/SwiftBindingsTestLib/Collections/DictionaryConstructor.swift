// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Constructor with Dictionary (HTTP-headers / dictionary-text-provider pattern)

/// Class taking Dictionary in constructor.
/// Real-world pattern: HTTPHeaders(IDictionary), DictionaryTextProvider(dict).
public class HeaderMap {
    private var headers: [String: String]

    public init(headers: [String: String]) {
        self.headers = headers
    }

    public func count() -> Int32 { Int32(headers.count) }
    public func get(_ key: String) -> String? { headers[key] }
    public func set(_ key: String, _ value: String) { headers[key] = value }
}

// MARK: - O1: Dictionary Property (get/set)

/// Class with a dictionary stored property (read-write).
/// Tests SwiftDictionary marshalling in both getter and setter directions.
public class PropertyBag {
    public var properties: [String: String]

    public init(properties: [String: String] = [:]) {
        self.properties = properties
    }

    public func count() -> Int32 { Int32(properties.count) }
}
