// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// IngestionBase — the transitive base module of the ingestion closure-preflight fixture. It is
// imported (and @_exported re-exported) by IngestionBridge, whose public surface names these types.
// The input-graph preflight gate builds this module and supplies it as a dependency in the CLOSED leg;
// in the MISSING-TRANSITIVE leg it is deliberately withheld so the preflight must name it as an
// unresolved obligation BEFORE any ABI parsing.

import CoreGraphics

public struct BaseValue {
    public let id: Int
    public init(id: Int) { self.id = id }
    public func describe() -> Int { id }
}

public protocol BaseProviding {
    var baseValue: BaseValue { get }
}

// Cross-module parent protocol for the dependency-quarantine leg (ingestion leg 4). IngestionBridge
// declares a protocol that INHERITS this one, so binding Bridge must resolve BaseSignal BY NAME out of the
// dependency protocol stash to lay out the inherited vtable slots. Leg 4 empties EXACTLY this protocol's
// mangled name in the DEPENDENCY ABI: its record is then malformed, and every primary construct that
// inherits or names it must be withdrawn — never emitted against the bad record. BaseProviding above stays
// healthy in that leg, so a Bridge protocol inheriting IT is the control that must survive untouched.
public protocol BaseSignal {
    func fire() -> Int
}

// --- Clang C-aggregate re-export stub, CROSS-MODULE seeding arm --------------------------------
//
// A retroactive conformance onto a C aggregate imported from a system module makes the digester
// emit a node for that aggregate in THIS module's ABI JSON. Such a node is a foreign re-export
// stub: its identity is the Clang USR (`c:@S@CGPoint`), it is flagged `isExternal`, and it
// legitimately carries NO Swift mangled name because the type is not declared in Swift at all.
// That absence must never be read as a malformed type record — quarantining the stub withdraws
// every declaration that stores the aggregate, and, because this module is consumed as a
// DEPENDENCY, propagates the withdrawal into the primary module through the cross-module
// quarantined-name set. BaseAnchor is the local casualty; IngestionBridge.BridgeAnchorHolder is
// the cross-module one.
public protocol BaseMeasuring {
    var baseMeasure: Double { get }
}

extension BaseMeasuring {
    public var baseMeasure: Double { 0 }
}

extension CGPoint: BaseMeasuring {}

public struct BaseAnchor {
    public let origin: CGPoint
    public init(origin: CGPoint) { self.origin = origin }
    public func anchorX() -> Double { Double(origin.x) }
}
