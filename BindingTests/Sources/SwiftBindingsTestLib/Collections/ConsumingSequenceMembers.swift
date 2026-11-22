// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Public members whose declaration carries a modifier after `public`, or whose name is a keyword.
// A `.swiftinterface` prints a Sequence conformer's iterator factory as
// `public __consuming func makeIterator()`, and a keyword-named method backticked
// (``public func `repeat`(count:)``). The interface reader once accepted a member as public only when
// every modifier after `public` was on an allow-list and its name was a plain identifier, so both
// members read as module-internal and were withdrawn from the binding.

public struct ConsumingTally: Sequence {
    public struct Iterator: IteratorProtocol {
        var current: Int
        let limit: Int
        public mutating func next() -> Int? {
            guard current < limit else { return nil }
            current += 1
            return current
        }
    }

    let limit: Int

    public init(count: Int) { limit = count }

    public var count: Int { limit }

    public __consuming func makeIterator() -> Iterator { Iterator(current: 0, limit: limit) }

    /// A tally `count` times as long.
    public func `repeat`(count times: Int) -> ConsumingTally { ConsumingTally(count: limit * times) }

    /// The tally extended by `extra`, keyword-named and with a default.
    public func `default`(extra: Int = 1) -> ConsumingTally { ConsumingTally(count: limit + extra) }
}
