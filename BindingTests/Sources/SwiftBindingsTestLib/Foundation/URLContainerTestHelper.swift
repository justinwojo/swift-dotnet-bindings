// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// Test helper class for URL container bridge projection tests.
/// Exercises array, dictionary, set, and nested container paths for Foundation.URL.
public class URLContainerTestHelper {

    public init() {}

    // MARK: - Array

    public func getURLArray() -> [URL] {
        return [URL(string: "https://example.com")!, URL(string: "https://test.com")!]
    }

    public func acceptURLArray(urls: [URL]) -> Int {
        return urls.count
    }

    // MARK: - Dictionary

    public func getURLDictionary() -> [String: URL] {
        return ["home": URL(string: "https://example.com")!, "api": URL(string: "https://api.example.com")!]
    }

    // MARK: - Set

    public func getURLSet() -> Set<URL> {
        return Set([URL(string: "https://example.com")!, URL(string: "https://test.com")!])
    }

    public func acceptURLSet(urls: Set<URL>) -> Int {
        return urls.count
    }

    // MARK: - Nested Array

    public func getNestedURLArray() -> [[URL]] {
        return [
            [URL(string: "https://a.com")!],
            [URL(string: "https://b.com")!, URL(string: "https://c.com")!]
        ]
    }

    // MARK: - Empty containers

    public func getEmptyURLArray() -> [URL] {
        return []
    }

    public func acceptEmptyURLArray(urls: [URL]) -> Int {
        return urls.count
    }
}
