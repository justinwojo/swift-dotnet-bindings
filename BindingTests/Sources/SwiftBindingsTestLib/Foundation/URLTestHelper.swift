// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// Test helper class for URL bridge projection tests.
/// Exercises scalar, optional, and property accessor paths for Foundation.URL.
public class URLTestHelper {
    public var storedURL: URL

    public init(url: URL) {
        self.storedURL = url
    }

    // Scalar param + return
    public func getURL() -> URL {
        return storedURL
    }

    public func setURL(url: URL) {
        storedURL = url
    }

    // Optional param + return
    public func getOptionalURL() -> URL? {
        return storedURL
    }

    public func acceptOptionalURL(url: URL?) -> Bool {
        if let url = url {
            storedURL = url
            return true
        }
        return false
    }

    // Property accessor
    public var url: URL {
        get { storedURL }
        set { storedURL = newValue }
    }

    public var optionalURL: URL? {
        get { storedURL }
    }
}
