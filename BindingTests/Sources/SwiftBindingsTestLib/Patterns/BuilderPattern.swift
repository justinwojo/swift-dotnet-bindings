// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Builder Pattern

/// Class with chained builder methods returning Self.
public class RequestBuilder {
    public var url: String
    public var method: String
    public var timeout: Int32
    public var retryCount: Int32

    public init(url: String) {
        self.url = url
        self.method = "GET"
        self.timeout = 30
        self.retryCount = 0
    }

    public func withMethod(_ method: String) -> RequestBuilder {
        self.method = method
        return self
    }

    public func withTimeout(_ timeout: Int32) -> RequestBuilder {
        self.timeout = timeout
        return self
    }

    public func withRetryCount(_ count: Int32) -> RequestBuilder {
        self.retryCount = count
        return self
    }

    public func describe() -> String { "\(method) \(url) timeout=\(timeout) retries=\(retryCount)" }
}
