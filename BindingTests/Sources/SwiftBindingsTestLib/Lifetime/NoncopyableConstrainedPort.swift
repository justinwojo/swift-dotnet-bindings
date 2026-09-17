// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A generic ~Copyable handle with members on constrained extensions
//
// Modelled on a kernel port: `NcPort<Right>` is a move-only handle with members on the type itself
// and on `where Right == …` extensions. A constrained-extension wrapper must BORROW the handle for a
// getter and CONSUME it for a `consuming` method, and must never copy it — a copy of a
// `~Copyable` value does not compile.

public protocol NcPortRight {}

public struct NcReceiveRight: NcPortRight {}

public struct NcSendRight: NcPortRight {}

public struct NcPort<Right: NcPortRight>: ~Copyable {
    internal var _name: UInt32

    public init(name: UInt32) { _name = name }

    // Unconstrained members. Their wrappers dispatch through the generic parent or a per-right
    // specialization; both must reach the value in place rather than copying it.

    public var name: UInt32 { _name }

    public var tag: UInt32 {
        get { _name &* 2 }
        set { _name = newValue / 2 }
    }

    public borrowing func peek() -> UInt32 { _name }

    public mutating func advance() -> UInt32 {
        _name &+= 1
        return _name
    }

    public consuming func close() -> UInt32 {
        let name = _name
        discard self
        return name &+ 1000
    }

    deinit {}
}

extension NcPort where Right == NcReceiveRight {
    /// Borrowing read of the handle.
    public var sendName: UInt32 { _name &+ 100 }

    /// Ends the handle's life and hands back its name.
    public consuming func relinquish() -> UInt32 {
        let name = _name
        discard self
        return name
    }
}

extension NcPort where Right == NcSendRight {
    public consuming func relinquish() -> UInt32 {
        let name = _name
        discard self
        return name &+ 1
    }
}

public struct NcCounter<Right> {
    public var value: Int

    public init(value: Int) { self.value = value }
}

extension NcCounter where Right == NcReceiveRight {
    /// A mutating member on a constrained extension writes back through the handle.
    public mutating func bump() -> Int {
        value += 1
        return value
    }
}

// MARK: - A ~Copyable generic with no conformers to specialize over
//
// With an unconstrained parameter there is no concrete pairing, so every member dispatches through
// the generic parent's metadata. That dispatch must still borrow, mutate in place and consume.

public struct NcSlot<Element>: ~Copyable {
    internal var _count: Int

    public init(count: Int) { _count = count }

    public var count: Int { _count }

    public borrowing func peek() -> Int { _count }

    public mutating func bump() -> Int {
        _count += 1
        return _count
    }

    public consuming func drain() -> Int {
        let count = _count
        discard self
        return count
    }

    public func echo(_ value: Element) -> Element { value }

    deinit {}
}
