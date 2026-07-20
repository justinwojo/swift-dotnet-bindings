// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// IngestionBase — the transitive base module of the ingestion closure-preflight fixture. It is
// imported (and @_exported re-exported) by IngestionBridge, whose public surface names these types.
// The input-graph preflight gate builds this module and supplies it as a dependency in the CLOSED leg;
// in the MISSING-TRANSITIVE leg it is deliberately withheld so the preflight must name it as an
// unresolved obligation BEFORE any ABI parsing.

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
