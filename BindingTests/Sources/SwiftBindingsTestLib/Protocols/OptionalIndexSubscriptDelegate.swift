// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - An optional subscript index, driven from Swift with nil and with zero
//
// A subscript index is an ordinary reverse-dispatch INPUT and has to be read the way a method
// parameter is. Read as a raw `SwiftOptional<Int32>` carrier and handed straight to the managed
// indexer, an absent index takes the carrier's unconstrained `implicit operator T?` and reaches the
// implementation as a PRESENT zero — indistinguishable, on the C# side, from `.some(0)`.
//
// The fixture is built so those two cannot share a passing result: the driver calls with `nil`,
// with `.some(0)` and with a nonzero index, the implementation answers each with a different value,
// and the setter records the key it actually saw.

/// The delegate. One optional-index subscript, both accessors, so each emission site is covered.
public protocol OptionalIndexSubscriptDelegate: AnyObject {
    subscript(_ key: Int32?) -> Int32 { get set }
}

/// Weak-slot host, mirroring the callback sources these subscripts show up in.
public final class OptionalIndexSubscriptHost {
    public weak var delegate: OptionalIndexSubscriptDelegate?

    public init() {}

    public var hasDelegate: Bool {
        return delegate != nil
    }

    /// Getter with an ABSENT index. If nil collapses into a present zero on the way across, this
    /// returns whatever `readSome(0)` returns instead of its own answer.
    public func readNil() -> Int32 {
        guard let delegate = delegate else { return -1 }
        return delegate[nil]
    }

    /// Getter with a PRESENT index — including the zero that the collapse would forge.
    public func readSome(_ key: Int32) -> Int32 {
        guard let delegate = delegate else { return -1 }
        return delegate[key]
    }

    /// Setter with an ABSENT index.
    public func writeNil(_ value: Int32) {
        delegate?[nil] = value
    }

    /// Setter with a PRESENT index.
    public func writeSome(_ key: Int32, _ value: Int32) {
        delegate?[key] = value
    }
}
