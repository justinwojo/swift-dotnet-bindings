// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - URL as Parameter

/// Returns the host component of the URL, or nil if not available.
public func urlHost(_ url: URL) -> String? {
    return url.host
}

// MARK: - Optional URL Return

/// Creates a URL from the given string. Returns nil if the string is not a valid URL.
public func makeURL(from string: String) -> URL? {
    return URL(string: string)
}

// MARK: - URL Manipulation

/// Appends a path component to the URL.
public func appendPath(_ url: URL, component: String) -> URL {
    return url.appendingPathComponent(component)
}

// MARK: - Struct with URL Property

/// Represents an API endpoint with a base URL and path.
public struct Endpoint {
    public var baseURL: URL
    public var path: String

    public init(baseURL: URL, path: String) {
        self.baseURL = baseURL
        self.path = path
    }

    /// The full URL combining baseURL and path.
    public var fullURL: URL {
        return baseURL.appendingPathComponent(path)
    }

    /// Returns the absolute string representation of the full URL.
    public func absoluteString() -> String {
        return fullURL.absoluteString
    }
}
