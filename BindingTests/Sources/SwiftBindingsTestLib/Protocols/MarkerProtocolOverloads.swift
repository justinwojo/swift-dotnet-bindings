// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Typed overloads of a marker-protocol parameter
//
// An empty protocol that only standard primitives conform to, through empty conformance
// extensions (the shape of a layout library's offset or inset target). A C# primitive cannot
// implement the protocol's interface, so the generator adds one overload per primitive conformer,
// each calling its own wrapper that takes the concrete value and lets Swift box it. Each overload
// is its own recovery unit, so a withdrawn member does not take its overloads with it.

/// The marker: no requirements, conformed to only by primitives.
public protocol LedgerAmountTarget {}

extension Int: LedgerAmountTarget {}
extension Double: LedgerAmountTarget {}
extension Float: LedgerAmountTarget {}
extension Int32: LedgerAmountTarget {}
extension Bool: LedgerAmountTarget {}

/// Describes the value an existential of the marker holds, naming its dynamic type.
func describeLedgerAmount(_ amount: any LedgerAmountTarget) -> String {
    switch amount {
    case let value as Int: return "Int:\(value)"
    case let value as Double: return "Double:\(value)"
    case let value as Float: return "Float:\(value)"
    case let value as Int32: return "Int32:\(value)"
    case let value as Bool: return "Bool:\(value)"
    default: return "unknown"
    }
}

public final class LedgerAmountSink {
    /// What the last call recorded.
    public private(set) var lastRecorded: String = ""

    public init() {}

    /// A void member taking only the marker.
    public func record(_ amount: any LedgerAmountTarget) {
        lastRecorded = describeLedgerAmount(amount)
    }

    /// A builder member returning the receiver.
    public func adding(_ amount: any LedgerAmountTarget) -> LedgerAmountSink {
        lastRecorded = "adding " + describeLedgerAmount(amount)
        return self
    }

    /// A member taking the marker and another argument. A typed overload has to pass both.
    public func recordScaled(_ amount: any LedgerAmountTarget, scale: Int32) {
        lastRecorded = describeLedgerAmount(amount) + " x\(scale)"
    }

    /// The last value a static member recorded.
    public nonisolated(unsafe) static var lastStaticRecorded: String = ""

    /// A static void member taking only the marker.
    public static func recordStatic(_ amount: any LedgerAmountTarget) {
        lastStaticRecorded = describeLedgerAmount(amount)
    }
}
