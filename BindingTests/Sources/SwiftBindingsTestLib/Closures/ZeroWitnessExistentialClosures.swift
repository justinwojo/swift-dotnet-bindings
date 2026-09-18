// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Zero-witness existentials crossing a closure boundary
//
// `Any` and `any Sendable` have no witness table: across a closure boundary they travel as the
// four-word ExistentialContainer0, and C# sees them as `object`. A C# callback Swift calls with
// an `Any` argument is bound. A closure that returns `Any` is refused in both directions: Swift
// returns the container indirectly, while a CallConvSwift signature returns a four-word struct in
// registers, so neither side would read what the other wrote. Those members stay here as refusal
// fixtures.
//
// The returned closures that also take a struct argument exercise the invoker paths that marshal
// frozen and non-frozen struct arguments alongside the existential one.

/// Frozen value type, so a closure taking it marshals the struct through a stack buffer.
@frozen
public struct ZeroWitnessClosurePoint {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// Resilient value type, so a closure taking it marshals the struct through a heap buffer.
public struct ZeroWitnessClosureTag {
    public var name: String

    public init(name: String) {
        self.name = name
    }
}

private func zeroWitnessClosureDescribe(_ value: Any) -> String {
    "\(type(of: value)):\(value)"
}

public class ZeroWitnessClosureHost: NSObject {
    public override init() {
        super.init()
    }

    // MARK: C# callbacks called by Swift

    /// Swift passes an `Int` into the C# callback.
    public func callWithAnyInt(_ body: (Any) -> String) -> String {
        body(42)
    }

    /// Swift passes a `String` into the C# callback.
    public func callWithAnyString(_ body: (Any) -> String) -> String {
        body("swift-side")
    }

    public func callWithSendable(_ body: (any Sendable) -> String) -> String {
        body(7)
    }

    /// The C# callback's `Any` result is consumed by Swift. Refused: `Any` closure return.
    public func describeProducedAny(_ body: () -> Any) -> String {
        zeroWitnessClosureDescribe(body())
    }

    /// Refused: `any Sendable` closure return.
    public func describeProducedSendable(_ body: () -> any Sendable) -> String {
        zeroWitnessClosureDescribe(body())
    }

    /// Escaping callback: stored, then called once. Refused: `Any` closure return.
    public func describeProducedAnyEscaping(_ body: @escaping () -> Any) -> String {
        let stored = body
        return zeroWitnessClosureDescribe(stored())
    }

    // MARK: Swift closures called by C#

    /// C# passes a value into the returned Swift closure. Not bound today: an invoker that takes an
    /// existential argument has no function-pointer marshaler, so the member is refused.
    public func makeAnyDescriber() -> (Any) -> String {
        { zeroWitnessClosureDescribe($0) }
    }

    /// C# would read the value the returned Swift closure produces. Refused: `Any` closure return.
    public func makeAnyProducer(_ value: Int32) -> () -> Any {
        { value }
    }

    /// Refused: `Any` closure return.
    public func makeStringProducer(_ value: String) -> () -> Any {
        { value }
    }

    /// Refused: `any Sendable` closure return.
    public func makeSendableProducer(_ value: Int32) -> () -> any Sendable {
        { value }
    }

    /// The returned closure takes a frozen struct next to the existential.
    public func makePointAnyDescriber() -> (ZeroWitnessClosurePoint, Any) -> String {
        { point, value in "(\(point.x),\(point.y))|\(zeroWitnessClosureDescribe(value))" }
    }

    /// The returned closure takes a non-frozen struct next to the existential.
    public func makeTagAnyDescriber() -> (ZeroWitnessClosureTag, Any) -> String {
        { tag, value in "\(tag.name)|\(zeroWitnessClosureDescribe(value))" }
    }
}
