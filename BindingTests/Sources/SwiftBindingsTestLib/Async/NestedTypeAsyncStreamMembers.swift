// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - AsyncStream members spelled through a nested type chain
//
// A class nested in an extension of a caseless namespace enum names its stream through its own
// qualified path (`StreamTasks.Watcher.Stream.Continuation`) and a typealias. The wrapper has to
// resolve every segment of that chain, not just the outermost name.

public enum StreamTasks {}

extension StreamTasks {
    public final class Watcher {
        public typealias Stream = AsyncStream<StreamTasks.Watcher.StreamEvent>

        public enum StreamEvent {
            case changed(Int)
        }

        /// The continuation feeding `events`, set once `start()` runs.
        public var continuation: StreamTasks.Watcher.Stream.Continuation?

        private var received: [Int] = []

        public init() {}

        public var hasContinuation: Bool { continuation != nil }

        /// Opens the event stream and records every event it carries.
        public func start() {
            let stream = Stream { self.continuation = $0 }
            Task {
                for await event in stream {
                    if case .changed(let value) = event { self.received.append(value) }
                }
            }
        }

        public func emit(_ value: Int) {
            continuation?.yield(.changed(value))
        }

        public var receivedCount: Int { received.count }

        /// A finite stream of `0..<count` read back through a nested member.
        public func counting(to count: Int) -> AsyncStream<Int> {
            AsyncStream { continuation in
                for value in 0..<count { continuation.yield(value) }
                continuation.finish()
            }
        }

        public static var ticks: AsyncStream<Int> {
            AsyncStream { continuation in
                continuation.yield(1)
                continuation.yield(2)
                continuation.finish()
            }
        }
    }
}
