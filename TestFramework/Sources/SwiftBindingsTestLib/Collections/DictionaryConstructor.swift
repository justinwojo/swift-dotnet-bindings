// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Constructor with Dictionary (Alamofire HTTPHeaders pattern)

/// Class taking Dictionary in constructor.
/// Real-world pattern: Alamofire HTTPHeaders(IDictionary), Lottie DictionaryTextProvider(dict).
public class HeaderMap {
    private var headers: [String: String]

    public init(headers: [String: String]) {
        self.headers = headers
    }

    public func count() -> Int32 { Int32(headers.count) }
    public func get(_ key: String) -> String? { headers[key] }
    public func set(_ key: String, _ value: String) { headers[key] = value }
}
