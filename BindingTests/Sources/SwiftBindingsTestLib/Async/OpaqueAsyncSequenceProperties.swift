// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Opaque AsyncSequence properties
// A property typed `some AsyncSequence<Element, Failure>` hides its concrete sequence and promises
// only the element and failure types. The binding iterates it in a Swift wrapper exactly as it
// iterates an AsyncStream, so it projects as IAsyncEnumerable<Element>: throwing unless Failure is
// Never. The concrete sequences below are internal on purpose, as a framework's would be.

public struct OpaqueSequenceLevel: Sendable {
    public let index: Int32
    public let value: Float
    public init(index: Int32, value: Float) {
        self.index = index
        self.value = value
    }
}

public enum OpaqueSequenceError: Error {
    case interrupted(after: Int32)
}

struct CountingLevelSequence: AsyncSequence, Sendable {
    typealias Element = OpaqueSequenceLevel
    let count: Int32
    let failAfter: Int32?

    struct AsyncIterator: AsyncIteratorProtocol {
        var position: Int32 = 0
        let count: Int32
        let failAfter: Int32?

        mutating func next() async throws -> OpaqueSequenceLevel? {
            if let failAfter, position == failAfter {
                throw OpaqueSequenceError.interrupted(after: failAfter)
            }
            guard position < count else { return nil }
            await Task.yield()
            defer { position += 1 }
            return OpaqueSequenceLevel(index: position, value: Float(position) * 0.5)
        }
    }

    func makeAsyncIterator() -> AsyncIterator {
        AsyncIterator(count: count, failAfter: failAfter)
    }
}

private func tickStream(_ count: Int32) -> AsyncStream<Int32> {
    AsyncStream { continuation in
        for tick in 1...count { continuation.yield(tick) }
        continuation.finish()
    }
}

/// An actor whose nonisolated property hands out an opaque, throwing, Sendable sequence.
@available(iOS 18.0, macOS 15.0, tvOS 18.0, watchOS 11.0, *)
public actor OpaqueLevelMeter {
    private let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    /// Yields `count` levels (index i, value i / 2), then finishes.
    public nonisolated var levels: some Sendable & AsyncSequence<OpaqueSequenceLevel, any Error> {
        CountingLevelSequence(count: count, failAfter: nil)
    }

    /// Yields two levels, then throws `OpaqueSequenceError.interrupted(after: 2)`.
    public nonisolated var interruptedLevels: some Sendable & AsyncSequence<OpaqueSequenceLevel, any Error> {
        CountingLevelSequence(count: count, failAfter: 2)
    }

    /// Never fails: yields 1 through `count`.
    public nonisolated var ticks: some AsyncSequence<Int32, Never> & Sendable {
        tickStream(count)
    }
}

/// A class whose property hands out an opaque sequence that never fails.
@available(iOS 18.0, macOS 15.0, tvOS 18.0, watchOS 11.0, *)
public final class OpaqueTickSource {
    private let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    /// Yields 1 through `count`.
    public var ticks: some AsyncSequence<Int32, Never> {
        tickStream(count)
    }
}
