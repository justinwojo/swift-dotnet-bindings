// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Default Parameters (Tier 2)

/// Function with a default parameter.
public func greet(name: String, greeting: String = "Hello") -> String {
    return "\(greeting), \(name)!"
}

/// Function with multiple default parameters.
public func search(query: String, limit: Int32 = 10, offset: Int32 = 0) -> String {
    return "Search '\(query)' limit=\(limit) offset=\(offset)"
}

/// Function with mixed required and default parameters.
public func configure(host: String, port: Int32 = 8080, secure: Bool = true) -> String {
    let scheme = secure ? "https" : "http"
    return "\(scheme)://\(host):\(port)"
}

// MARK: - inout Parameter With Trailing Defaults (bug b)
//
// SwiftRichString's `to: inout NSMutableAttributedString`-adjacent shape: an `inout`
// parameter sitting alongside trailing default parameters. Pre-fix,
// `DefaultParameterOverloadEmitter`'s trimmed-overload shim re-derived the trimmed
// parameter list without preserving the `inout` qualifier on the still-present
// parameter, so the trimmed overload's internal call passed a plain value where the
// underlying method required a mutable reference — a swiftc compile failure.

/// Increments `value` in place, applies `scale`, and returns a formatted label.
/// The trimmed (no-default-args) overload must still declare `value` as `inout`.
public func adjustAndLog(_ value: inout Int32, tag: String = "default", scale: Int32 = 1) -> String {
    value = (value + 1) * scale
    return "\(tag): \(value)"
}
