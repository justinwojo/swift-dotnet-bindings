// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - AsyncSequence Layer A fixtures
//
// Mirrors the StoreKit `Transaction.Transactions : AsyncSequence` and
// MusicKit `MusicSubscription.Updates : AsyncSequence` shape: a non-frozen
// struct with a nested AsyncIterator producing a non-throwing element
// stream. The C# side must adopt `IAsyncEnumerable<Element>` so consumers
// can use the canonical `await foreach (var x in seq)` pattern instead of
// hand-rolling `MakeAsyncIterator()` / `NextAsync()` loops.

/// AsyncSequence-conforming type that yields a fixed sequence of integers
/// terminated by a sentinel iteration count. Mirrors the
/// `StoreKit.Transaction.Transactions` shape: a non-frozen struct with a
/// nested struct iterator over a primitive Element.
public struct CounterSequence: AsyncSequence, Sendable {
    public typealias Element = Int32

    public let upTo: Int32

    public init(upTo: Int32) {
        self.upTo = upTo
    }

    public struct AsyncIterator: AsyncIteratorProtocol {
        public typealias Element = Int32

        var current: Int32 = 0
        let limit: Int32

        public mutating func next() async -> Int32? {
            // Tiny suspension so the call goes through the actual async
            // wrapper path rather than an inlined fast path.
            try? await Task.sleep(nanoseconds: 1_000_000)
            guard current < limit else { return nil }
            current += 1
            return current
        }
    }

    public func makeAsyncIterator() -> AsyncIterator {
        AsyncIterator(current: 0, limit: upTo)
    }
}

/// AsyncSequence-conforming type whose Element is a non-frozen struct.
/// Tests that the IAsyncEnumerable&lt;T&gt; bridge handles a SafeHandle-
/// backed Element (the shape that backs `StoreKit.Transaction.Transactions`
/// → `VerificationResult&lt;Transaction&gt;` and
/// `MusicKit.MusicSubscription.Updates` → `MusicSubscription`).
public struct ScoreUpdateSequence: AsyncSequence, Sendable {
    public typealias Element = ScoreUpdate

    public let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    public struct AsyncIterator: AsyncIteratorProtocol {
        public typealias Element = ScoreUpdate

        var index: Int32 = 0
        let limit: Int32

        public mutating func next() async -> ScoreUpdate? {
            try? await Task.sleep(nanoseconds: 1_000_000)
            guard index < limit else { return nil }
            index += 1
            return ScoreUpdate(round: index, points: index * 10)
        }
    }

    public func makeAsyncIterator() -> AsyncIterator {
        AsyncIterator(index: 0, limit: count)
    }
}

/// Element type for `ScoreUpdateSequence`. Plain non-frozen struct so it
/// surfaces in C# as a `SafeHandle`-backed reference type — the same shape
/// the StoreKit `VerificationResult` / MusicKit `MusicSubscription`
/// elements use.
public struct ScoreUpdate: Sendable {
    public let round: Int32
    public let points: Int32

    public init(round: Int32, points: Int32) {
        self.round = round
        self.points = points
    }
}

/// AsyncSequence-conforming type whose Element is `String`. Pins the
/// projection-aware element-type translation: NextAsync emits
/// `Task<string?>` (StringProjection.PublicType), so the
/// `IAsyncEnumerable<T>` bridge MUST also use `string` — otherwise the
/// `yield return` of the projected value into an
/// `IAsyncEnumerable<Swift.SwiftString>` fails CS0029 at compile time.
/// Mirrors any AsyncSequence whose Element is a Swift stdlib type that
/// projects through TypeProjectionFactory (Swift.String → string,
/// Foundation.Data → byte[], Foundation.Date → double).
public struct LabelSequence: AsyncSequence, Sendable {
    public typealias Element = String

    public let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    public struct AsyncIterator: AsyncIteratorProtocol {
        public typealias Element = String

        var index: Int32 = 0
        let limit: Int32

        public mutating func next() async -> String? {
            try? await Task.sleep(nanoseconds: 1_000_000)
            guard index < limit else { return nil }
            index += 1
            return "label-\(index)"
        }
    }

    public func makeAsyncIterator() -> AsyncIterator {
        AsyncIterator(index: 0, limit: count)
    }
}
