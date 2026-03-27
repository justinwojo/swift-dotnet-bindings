// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - C# Keyword Collision Tests

/// Struct with properties that collide with C# reserved keywords.
/// The binding generator must escape these names in C# output.
public struct KeywordTest {
    /// "event" is a C# keyword.
    public var event: String

    /// "delegate" is a C# keyword.
    public var delegate: String

    /// "operator" is a C# keyword.
    public var `operator`: String

    /// "class" is a C# keyword (using backtick-escaped Swift keyword).
    public var `class`: String

    public init(event: String, delegate: String, `operator`: String, `class`: String) {
        self.event = event
        self.delegate = delegate
        self.operator = `operator`
        self.class = `class`
    }

    /// Method with a parameter name that's a C# keyword.
    public func format(using namespace: String) -> String {
        return "\(namespace): \(event), \(delegate)"
    }
}

// MARK: - Swift Keyword Tests

/// Function using backtick-escaped Swift keywords as identifiers.
public func processKeywords(`in` input: String, `for` target: String) -> String {
    return "\(target): \(input)"
}

// MARK: - Simpler Backtick/Keyword Tests
// The KeywordTest struct above has 4 string params and hits the GPR overflow ABI bug.
// These simpler functions test backtick keyword handling without hitting that limit.

/// Function with a backtick-escaped keyword parameter label.
public func getKeywordValue(`for` key: String) -> String {
    return "value-for-\(key)"
}

/// Function with a backtick-escaped keyword parameter label and a primitive.
public func processKeywordParam(`class` name: String, count: Int32) -> String {
    return "\(name):\(count)"
}

// MARK: - Enum Cases with Keyword Labels (S1 pattern from Alamofire)
// See FilterScope.swift.disabled — the actual enum fixture for this pattern.
// Disabled because the generator produces invalid compound identifiers (`__@in`).
// When S1 is fixed, rename FilterScope.swift.disabled → FilterScope.swift.
