// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug 7: Property literally named `self` on a nested struct

/// Reproduces RealityFoundation.BindTarget where nested structs expose a
/// declared `var self: Outer` accessor. The generator must skip wrapper
/// emission for these — `obj.self` returns the receiver type, not the
/// declared return type, so a wrapper would emit an invalid cast.
public struct BugReproBindTarget {
    public var rawId: Int32

    public init(rawId: Int32) { self.rawId = rawId }

    public struct ScenePath {
        public let segments: [Int32]
        public init(segments: [Int32]) { self.segments = segments }
        public var `self`: BugReproBindTarget {
            BugReproBindTarget(rawId: Int32(segments.reduce(0, +)))
        }
    }
}

// MARK: - Bug 9: Mutating struct getter (lazy-style)

/// Reproduces a mutating getter — a `lazy var` exposes a stored property
/// whose getter is `mutating` because it may write the cached value back.
/// The generator's wrapper body cannot use `let obj = ...pointee` here;
/// it must rebind through `var obj` (or pointer access) to allow mutation.
public struct BugReproMutatingGetter {
    public var seed: Int32
    public lazy var derived: Int32 = seed * 2

    public init(seed: Int32) { self.seed = seed }
}

// MARK: - Bug 11: Subscript returning a tuple

/// Reproduces a subscript whose return type is a Swift tuple of blittable
/// elements. The generator must project the type as a C# value tuple
/// (`(int, int)`) — not the Swift `(Int32, Int32)` syntax that broke
/// `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp` in
/// RealityFoundation's Subscriptions API. (String/non-blittable elements in
/// tuples surface a separate, pre-existing marshalling gap.)
public class BugReproTupleSubscript {
    private var pairs: [(Int32, Int32)] = []

    public init() {}

    public func append(_ first: Int32, second: Int32) {
        pairs.append((first, second))
    }

    public subscript(index: Int32) -> (Int32, Int32) {
        get { pairs[Int(index)] }
        set { pairs[Int(index)] = newValue }
    }

    public func count() -> Int32 { Int32(pairs.count) }
}
