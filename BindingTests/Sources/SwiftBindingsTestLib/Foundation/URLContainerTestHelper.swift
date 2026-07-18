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

    // MARK: - Nested Array Parameter

    public func acceptNestedURLArray(urls: [[URL]]) -> Int {
        return urls.reduce(0) { $0 + $1.count }
    }

    // MARK: - Empty containers

    public func getEmptyURLArray() -> [URL] {
        return []
    }

    public func acceptEmptyURLArray(urls: [URL]) -> Int {
        return urls.count
    }

    // MARK: - Dictionary with Numeric Key (Bug fix #20)
    //
    // Pins the NSDictionary→Dictionary integer-key unboxing fix in
    // DictionaryProjection.FromNSObject. The container bridges to NSDictionary
    // because the value type (URL) is ObjC-bridgeable; the keys are then stored
    // as boxed NSNumber instances. Before the fix, `(nint)_nsKey` was emitted
    // and produced CS0030 because NSObject is not directly castable to nint.
    // Mirrors the RealityFoundation `[Int: URL]` shape that triggered this.

    public func getURLsBySample() -> [Int: URL] {
        return [
            10: URL(string: "https://sample-10.example.com")!,
            42: URL(string: "https://sample-42.example.com")!,
        ]
    }

    // MARK: - Scalar URL parameter (string convenience overload)
    //
    // A method taking a single non-optional scalar `URL` parameter, which projects
    // to a `Foundation.NSUrl` primary in C#. The generator emits an additive
    // `string`-taking overload alongside it, forwarding through `new NSUrl(s)`, so a
    // C# caller can pass the URL string directly without hand-constructing an NSUrl.

    public func describeURL(url: URL) -> String {
        return url.absoluteString
    }
}
