// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Callbacks Swift stores and invokes on its own
//
// Swift creates the closures from managed delegates and calls them when it chooses: later, from
// several of its own threads at once, from inside another callback, after the managed side has
// dropped every reference to the delegate. No native test driver calls the callbacks; every
// invocation below is Swift code invoking a closure it holds.

/// Two words returned in two general registers.
@frozen
public struct CallbackPair {
    public var first: Int64
    public var second: Int64

    public init(first: Int64, second: Int64) {
        self.first = first
        self.second = second
    }
}

/// Four words, which Swift returns through an indirect result buffer.
@frozen
public struct CallbackQuad {
    public var a: Int64
    public var b: Int64
    public var c: Int64
    public var d: Int64

    public init(a: Int64, b: Int64, c: Int64, d: Int64) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
    }
}

/// Holds escaping callbacks in numbered slots and invokes them on request.
public final class CallbackRelay {
    private let lock = NSLock()
    private var slots: [Int: (Int64) -> Int64] = [:]
    private var released = true
    private var caught = 0

    public init() {}

    /// Stores `body` in `slot`, replacing (and releasing) what the slot held.
    public func store(slot: Int, _ body: @escaping (Int64) -> Int64) {
        lock.lock()
        let previous = slots.updateValue(body, forKey: slot)
        lock.unlock()
        _ = previous
    }

    /// Invokes the callback in `slot` outside the lock, so the callback may call back into the relay.
    /// Returns `Int64.min` when the slot is empty.
    public func invoke(slot: Int, value: Int64) -> Int64 {
        lock.lock()
        let body = slots[slot]
        lock.unlock()
        guard let body else { return Int64.min }
        return body(value)
    }

    /// Invokes every stored callback once with `value` and returns the sum of the results.
    public func invokeAll(value: Int64) -> Int64 {
        lock.lock()
        let bodies = Array(slots.values)
        lock.unlock()
        var sum: Int64 = 0
        for body in bodies { sum &+= body(value) }
        return sum
    }

    /// Releases the callback in `slot`.
    public func clear(slot: Int) {
        lock.lock()
        let previous = slots.removeValue(forKey: slot)
        lock.unlock()
        _ = previous
    }

    /// Releases every stored callback.
    public func clearAll() {
        lock.lock()
        let previous = slots
        slots = [:]
        lock.unlock()
        _ = previous
    }

    public var storedCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return slots.count
    }

    /// Invokes the callback in `slot` from `threads` concurrent Swift threads, `iterations` times on
    /// each, with the values 0 ..< threads * iterations, and returns the sum of the results.
    public func invokeConcurrently(slot: Int, threads: Int, iterations: Int) -> Int64 {
        lock.lock()
        let body = slots[slot]
        lock.unlock()
        guard let body, threads > 0, iterations > 0 else { return Int64.min }
        var sums = [Int64](repeating: 0, count: threads)
        sums.withUnsafeMutableBufferPointer { buffer in
            let base = buffer.baseAddress!
            DispatchQueue.concurrentPerform(iterations: threads) { thread in
                var sum: Int64 = 0
                for i in 0..<iterations {
                    sum &+= body(Int64(thread * iterations + i))
                }
                base[thread] = sum
            }
        }
        return sums.reduce(0, &+)
    }

    /// Two callbacks of one signature in one call: `first(value) * 1000 + second(value)`.
    public func both(_ first: (Int64) -> Int64, _ second: (Int64) -> Int64, value: Int64) -> Int64 {
        first(value) &* 1000 &+ second(value)
    }

    /// Calls a throwing callback and catches what it throws. Returns the callback's result, or -1
    /// when it threw. After a throw, `lastErrorReleased` says whether the caught error object was
    /// gone once the catch scope and its autorelease pool ended.
    public func invokeThrowing(_ value: Int64, _ body: (Int64) throws -> Int64) -> Int64 {
        weak var error: AnyObject?
        var result: Int64 = -1
        var threw = false
        autoreleasepool {
            do {
                result = try body(value)
            } catch let thrown {
                threw = true
                error = thrown as AnyObject
            }
        }
        if threw {
            lock.lock()
            caught += 1
            released = error == nil
            lock.unlock()
            return -1
        }
        return result
    }

    /// Whether the error the last throwing callback threw was released by the time Swift returned.
    public var lastErrorReleased: Bool {
        lock.lock()
        defer { lock.unlock() }
        return released
    }

    /// How many errors throwing callbacks have thrown into this relay.
    public var caughtErrors: Int {
        lock.lock()
        defer { lock.unlock() }
        return caught
    }

    /// A result returned in two registers: `first * 1000 + second`.
    public func invokePair(_ value: Int64, _ body: (Int64) -> CallbackPair) -> Int64 {
        let pair = body(value)
        return pair.first &* 1000 &+ pair.second
    }

    /// A result returned indirectly: `a * 1000 + b * 100 + c * 10 + d`.
    public func invokeQuad(_ value: Int64, _ body: (Int64) -> CallbackQuad) -> Int64 {
        let quad = body(value)
        return quad.a &* 1000 &+ quad.b &* 100 &+ quad.c &* 10 &+ quad.d
    }
}

// MARK: - An error a callback returns cooperatively

private nonisolated(unsafe) var callbackTrackedErrorDeinits: Int32 = 0
private final class CallbackTrackedError: Error {
    deinit { callbackTrackedErrorDeinits += 1 }
}

/// A Swift error handed to managed code at +1. Managed code owns that reference and releases it;
/// a callback may return it cooperatively any number of times in between.
public func makeCallbackTrackedError() -> Int64 {
    let error: any Error = CallbackTrackedError()
    return Int64(Int(bitPattern: Unmanaged.passRetained(error as AnyObject).toOpaque()))
}

/// How many `CallbackTrackedError` instances have been destroyed.
public func getCallbackTrackedErrorDeinits() -> Int32 { callbackTrackedErrorDeinits }
