// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// IngestionBridge — the PRIMARY module bound by the ingestion closure-preflight gate. It
// `@_exported import`s IngestionBase (re-exporting it into Bridge's public surface) and adds a
// RETROACTIVE conformance and a FOREIGN extension onto a Base type, so Bridge's public interface
// genuinely depends on IngestionBase at the type level — not merely as an import that could be
// dropped. Binding Bridge is therefore impossible unless IngestionBase is resolvable:
//   * CLOSED leg   — IngestionBase supplied as a dependency → the graph closes → generation succeeds.
//   * MISSING leg  — IngestionBase withheld → the preflight names it as an unresolved obligation
//                    (SWIFTBIND119) BEFORE ABI parsing, emitting no artifacts.

@_exported import IngestionBase

public protocol BridgeMarker {
    func marker() -> Int
}

// Retroactive conformance: conform a type OWNED BY IngestionBase to a protocol owned by IngestionBridge.
extension BaseValue: BridgeMarker {
    public func marker() -> Int { describe() }
}

// Foreign extension: add public API onto an IngestionBase type from the IngestionBridge module.
extension BaseValue {
    public var bridgedDouble: Int { describe() * 2 }
}

public struct BridgeWrapper: BaseProviding {
    public let baseValue: BaseValue
    public init(baseValue: BaseValue) { self.baseValue = baseValue }
    public func markerOfBase() -> Int { baseValue.marker() }
}

// --- Proven-closure quarantine fixture (ingestion leg 3) ---------------------------------------
//
// QuarantinedPayload is a fully bindable value type in the pristine build (leg 1 binds it cleanly).
// Leg 3 clones Bridge's ABI JSON and empties EXACTLY this type's `mangledName`, simulating a digester
// that emitted a malformed type record. The ingestion ledger must then QUARANTINE the type and drag
// its dependent-edge closure — the free functions whose signatures name it — into the same withdrawal,
// while HealthyControl (which shares no edge with it) survives byte-identically.

public struct QuarantinedPayload {
    public let token: Int
    public init(token: Int) { self.token = token }
    public func payloadValue() -> Int { token }
}

// HealthyControl shares NO signature, storage, or conformance edge with QuarantinedPayload, so the
// dependent-edge closure must never reach it. Its emitted surface is the control the gate diffs.
public struct HealthyControl {
    public let value: Int
    public init(value: Int) { self.value = value }
    public func controlValue() -> Int { value }
}

// Dependent edges on QuarantinedPayload: one return-typed, one parameter-typed. Both must be dragged
// into the quarantine — a retained reference to the withdrawn type would not compile.
public func makeQuarantinedPayload() -> QuarantinedPayload { QuarantinedPayload(token: 7) }
public func inspectQuarantined(_ payload: QuarantinedPayload) -> Int { payload.payloadValue() }

// Enum payload edge: PayloadCarrier's `.boxed` case embeds QuarantinedPayload in the enum's in-line
// layout, so the enum's storage is indeterminate once the payload is withdrawn — the whole enum must be
// withdrawn. `.empty` carries no payload; the enum still goes whole (its layout is one indivisible ABI).
public enum PayloadCarrier {
    case empty
    case boxed(QuarantinedPayload)
}

// Operator edge on a RETAINED host: `==` names QuarantinedPayload as an operand, but PayloadComparator
// itself has no structural edge to it. The operator must be withdrawn as a leaf while the host struct
// (and its healthy `compareCount`) survive.
public struct PayloadComparator {
    public let compareCount: Int
    public init(compareCount: Int) { self.compareCount = compareCount }
    public static func == (lhs: PayloadComparator, rhs: QuarantinedPayload) -> Bool {
        lhs.compareCount == rhs.token
    }
}

// An independent free function on the healthy control — must survive untouched.
public func makeHealthyControl() -> HealthyControl { HealthyControl(value: 11) }

// --- Cross-module dependency-quarantine fixture (ingestion leg 4) ------------------------------
//
// BridgeRelay INHERITS IngestionBase.BaseSignal across the module boundary, so the emitter resolves
// BaseSignal by name from the dependency protocol stash to lay out its inherited vtable slots. Leg 4
// quarantines BaseSignal (empties its mangled name in the DEPENDENCY ABI). BaseSignal's record is then
// malformed, so BridgeRelay's contract is indeterminate and it must be withdrawn whole with an
// IngestionWithdrawal row naming BaseSignal — the emitter must never consume the malformed dependency
// record by name and emit a vtable slot against it.
public protocol BridgeRelay: BaseSignal {
    func relaySignal() -> Int
}

// BridgeBeacon inherits BaseProviding, which STAYS HEALTHY in leg 4. It is the cross-module control: the
// dependency-quarantine withdrawal must reach only the malformed parent's descendants, so BridgeBeacon's
// emitted surface must survive byte-identically.
public protocol BridgeBeacon: BaseProviding {
    func beaconValue() -> Int
}
