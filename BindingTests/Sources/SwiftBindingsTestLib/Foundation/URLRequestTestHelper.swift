// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// Test helper class for URLRequest bridge projection tests.
/// Exercises scalar, optional, container, and property accessor paths for Foundation.URLRequest.
public class URLRequestTestHelper {

    public init() {}

    // MARK: - Scalar param + return

    /// Creates a URLRequest from a URL.
    public func createRequest(url: URL) -> URLRequest {
        return URLRequest(url: url)
    }

    /// Extracts the URL from a URLRequest, or nil if no URL is set.
    public func getRequestURL(request: URLRequest) -> URL? {
        return request.url
    }

    /// Returns the timeout interval of a URLRequest.
    public func getTimeout(request: URLRequest) -> Double {
        return request.timeoutInterval
    }

    /// Accepts an optional URLRequest and returns whether it was non-nil.
    public func acceptOptionalRequest(request: URLRequest?) -> Bool {
        return request != nil
    }

    /// Returns an optional URLRequest (always non-nil for testing).
    public func getOptionalRequest(url: URL) -> URLRequest? {
        return URLRequest(url: url)
    }

    // MARK: - Property accessor

    public var storedRequest: URLRequest = URLRequest(url: URL(string: "https://default.com")!)

    // MARK: - Container

    /// Returns an array of URLRequests.
    public func getRequestArray() -> [URLRequest] {
        return [
            URLRequest(url: URL(string: "https://a.com")!),
            URLRequest(url: URL(string: "https://b.com")!)
        ]
    }

    /// Accepts an array of URLRequests and returns the count.
    public func acceptRequestArray(requests: [URLRequest]) -> Int {
        return requests.count
    }
}
