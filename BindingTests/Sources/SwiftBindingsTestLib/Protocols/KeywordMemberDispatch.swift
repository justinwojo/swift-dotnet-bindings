// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Keyword-named protocol members (reverse dispatch)
//
// Distinct from the keyword-*label* fixtures in SiblingPropertyDispatch.swift:
// here the member NAMES themselves are Swift keywords (`repeat`, `class`). When a
// C# type implements this protocol, the generator emits an EveryProtocol
// conformance whose witness members are declared `public var `repeat`` /
// `public func `class`()`. Without backtick-escaping at those declaration sites the
// generated Swift fails to compile (a bare `public var repeat:` is a parse error),
// so the conformance — and therefore the whole wrapper — would never build.
//
// Members are blittable (Int32 get / Int32-returning method) so dispatch routes
// cleanly through the vtable, and the router lets a C# proxy round-trip a value
// back through each keyword-named member.

public protocol KeywordMemberDelegate {
    /// `repeat` is a Swift keyword — the conformance must emit `public var `repeat``.
    var `repeat`: Int32 { get }
    /// `class` is a Swift keyword — the conformance must emit `public func `class`()`.
    func `class`() -> Int32
}

/// Consumer that dispatches into a (possibly C#-backed) `KeywordMemberDelegate`.
public class KeywordMemberRouter {
    public var delegate: (any KeywordMemberDelegate)?

    public init() {
        self.delegate = nil
    }

    public func readRepeat() -> Int32 {
        return delegate?.`repeat` ?? -1
    }

    public func callClass() -> Int32 {
        return delegate?.`class`() ?? -1
    }
}
