// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A method's own generic parameter alongside a closure parameter
//
// A closure parameter is adapted by a Swift wrapper, and when the member also declares its own
// generic parameters that wrapper must be generic too. It was emitted with the generic parameters
// referenced but never declared, so the wrapper named `τ_0_0` with nothing binding it and did not
// compile; and where the wrapper is a free function taking `self` as an explicit parameter, the
// implicit metadata Swift appends trails EVERY declared parameter — `self` included — so the
// P/Invoke had to put `self` before the metadata rather than after it.
//
// Both halves are exercised below on the three receiver shapes (free function, instance method,
// static method), through a protocol the binding projects to C#: that conformance carries a
// witness table, which the P/Invoke passes alongside the metadata.

public protocol ScaleRule {
    var factor: Int { get }
}

public struct DoubleRule: ScaleRule {
    public let factor: Int
    public init(factor: Int = 2) { self.factor = factor }
}

public func scaleWithRule<R: ScaleRule>(_ rule: R, _ adjust: (Int) -> Int) -> Int {
    adjust(rule.factor)
}

public final class RuleBox {
    public let base: Int
    public init(base: Int) { self.base = base }

    public func apply<R: ScaleRule>(_ rule: R, _ adjust: (Int) -> Int) -> Int {
        base + adjust(rule.factor)
    }

    public static func applyStatic<R: ScaleRule>(_ rule: R, _ adjust: (Int) -> Int) -> Int {
        adjust(rule.factor) * 10
    }
}

// MARK: - The same shape constrained to a protocol the binding does not project
//
// `Swift.Sequence` is in no type database, so the generic conformance is dropped from the P/Invoke
// while the Swift entry point still expects its witness table trailing the metadata. The call would
// hand Swift a witness table it never received and fault on the first witness call, so the member
// is emitted as a throwing tombstone instead.

public func sumThroughSequence<T: Sequence>(_ values: T, _ adjust: (Int) -> Int) -> Int
where T.Element == Int {
    values.reduce(0) { $0 + adjust($1) }
}
