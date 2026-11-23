// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Case identity in Swift memory
//
// Each crossing below hands CaseShift over in Swift's own memory rather than as an argument or a
// result scalar: container elements, an Optional's payload, a tuple element, an async result, and
// a slot a C# conformer reads or fills. The runtime reads and writes those through the binding's
// case codec, so the hidden case's tag (first = 1, second = 2) never reaches C# as a constant.

public func caseShiftList() -> [CaseShift] { [.hidden, .first, .second] }
public func caseShiftListNames(_ values: [CaseShift]) -> String { values.map(caseShiftName).joined(separator: ",") }
public func caseShiftOptionalList() -> [CaseShift?] { [.first, nil, .hidden, .second] }
public func caseShiftOptionalListNames(_ values: [CaseShift?]) -> String {
    values.map { $0.map(caseShiftName) ?? "none" }.joined(separator: ",")
}
public func caseShiftByName() -> [String: CaseShift] { ["hidden": .hidden, "first": .first, "second": .second] }
public func caseShiftDictionaryNames(_ values: [String: CaseShift]) -> String {
    values.keys.sorted().map { "\($0)=\(caseShiftName(values[$0]!))" }.joined(separator: ",")
}
public func caseShiftSet() -> Set<CaseShift> { [.first, .second] }
public func caseShiftSetNames(_ values: Set<CaseShift>) -> String { values.map(caseShiftName).sorted().joined(separator: ",") }
public func caseShiftPair(_ count: Int) -> (CaseShift, Int) { (count == 0 ? .hidden : .first, count) }
public func caseShiftAsync(_ hidden: Bool) async -> CaseShift { hidden ? .hidden : .first }
public func caseShiftOptionalAsync(_ present: Bool) async -> CaseShift? { present ? .second : nil }

/// Optional-typed property and subscript getters, which hand C# the whole `Optional<CaseShift>`.
public final class CaseShiftOptionalBox {
    public var mode: CaseShift?
    public init(mode: CaseShift?) { self.mode = mode }
    public var modeName: String { mode.map(caseShiftName) ?? "none" }
    public subscript(slot: Int) -> CaseShift? { slot == 0 ? .hidden : slot == 1 ? .first : nil }
}

/// A requirement set a C# conformer implements: Swift passes CaseShift in, takes it back as a
/// result, reads it from a property, and hands it over `inout`.
public protocol CaseShiftResponder {
    var caseShiftCurrent: CaseShift { get }
    func respond(_ value: CaseShift) -> CaseShift
    func describe(_ value: CaseShift) -> String
    func adjust(_ value: inout CaseShift)
}

/// Drives a conformer and names every case Swift saw: `caseShiftCurrent`, the result of `respond`, and
/// `value` after `adjust`, each as Swift reads it.
public func caseShiftDriveResponder(_ responder: any CaseShiftResponder, _ value: CaseShift) -> String {
    var adjusted = value
    responder.adjust(&adjusted)
    return [caseShiftName(responder.caseShiftCurrent), caseShiftName(responder.respond(value)),
            responder.describe(value), caseShiftName(adjusted)].joined(separator: ",")
}
public func caseShiftDriveResponderHidden(_ responder: any CaseShiftResponder) -> String {
    caseShiftDriveResponder(responder, .hidden)
}

/// A frozen struct with a case-identity enum field: the field is read and written through the
/// struct's accessors, never as the struct's bytes.
@frozen public struct CaseShiftHolder {
    public var mode: CaseShift
    public var count: Int
    public init(mode: CaseShift, count: Int) { self.mode = mode; self.count = count }
    public var modeName: String { caseShiftName(mode) }
}
public func caseShiftHolderHidden() -> CaseShiftHolder { CaseShiftHolder(mode: .hidden, count: 7) }
public func caseShiftHolderName(_ holder: CaseShiftHolder) -> String { "\(caseShiftName(holder.mode))x\(holder.count)" }

// MARK: - A simple enum whose binary hides a payload case

/// Its interface publishes two empty cases, so the binding projects a simple C# enum; the binary
/// also holds an `@_spi` case with a String payload, so the enum (and its Optional) is a word-sized,
/// non-trivial value in Swift memory. A crossing that treats it as a one-byte tag copies, frees or
/// reads the wrong bytes.
public enum PayloadShift {
    @_spi(CaseIdentity) case named(String)
    @_spi(CaseIdentity) case tracked(PayloadShiftToken)
    case plain
    case other
}

/// The payload of the hidden `tracked` case: one shared instance whose retain count a test reads,
/// so a crossing that copies the hidden case out of a slot it owns, and never destroys the slot,
/// shows as a retain left behind.
@_spi(CaseIdentity) public final class PayloadShiftToken {
    public init() {}
}
private let payloadShiftToken = PayloadShiftToken()
public func payloadShiftTokenRetains() -> Int { Int(_getRetainCount(payloadShiftToken)) }
public func payloadShiftTrackedList() -> [PayloadShift] { [.plain, .tracked(payloadShiftToken), .other] }
public func payloadShiftTrackedPair(_ count: Int) -> (PayloadShift, Int) { (.tracked(payloadShiftToken), count) }
public func payloadShiftTrackedAsync(_ tracked: Bool) async -> PayloadShift { tracked ? .tracked(payloadShiftToken) : .plain }
/// A payload enum holding case-identity enums, alone and as a tuple element: reading a payload
/// back must name the case Swift holds, not the case whose C# constant equals the stored tag.
/// The tuple puts a byte first, so the enum sits at offset 1, as its own one-byte tag.
public enum CaseShiftCarrier {
    case shift(CaseShift)
    case pair(UInt8, CaseShift)
    case payload(PayloadShift)
    case empty
}

public func caseShiftCarrierHidden() -> CaseShiftCarrier { .shift(.hidden) }
public func caseShiftCarrierPair() -> CaseShiftCarrier { .pair(7, .second) }
public func caseShiftCarrierTracked() -> CaseShiftCarrier { .payload(.tracked(payloadShiftToken)) }
public func caseShiftCarrierName(_ carrier: CaseShiftCarrier) -> String {
    switch carrier {
    case .shift(let value): return "shift:" + caseShiftName(value)
    case .pair(let count, let value): return "pair:\(count):" + caseShiftName(value)
    case .payload(let value): return "payload:" + payloadShiftName(value)
    case .empty: return "empty"
    }
}

/// A generic result whose `T` is the enum itself, or a tuple holding it.
public func payloadShiftTrackedAs<T>(_ fallback: T) -> T {
    if let value = PayloadShift.tracked(payloadShiftToken) as? T { return value }
    if let value = (PayloadShift.tracked(payloadShiftToken), 0) as? T { return value }
    return fallback
}

public func payloadShiftName(_ value: PayloadShift) -> String {
    switch value {
    case .named(let text): return "named:\(text)"
    case .tracked: return "tracked"
    case .plain: return "plain"
    case .other: return "other"
    }
}
public func payloadShiftNameOrNone(_ value: PayloadShift?) -> String { value.map(payloadShiftName) ?? "none" }
public func payloadShiftOther() -> PayloadShift { .other }
public func payloadShiftOptional(_ present: Bool) -> PayloadShift? { present ? .other : nil }
public func payloadShiftList() -> [PayloadShift] { [.plain, .other, .plain] }
public func payloadShiftListNames(_ values: [PayloadShift]) -> String { values.map(payloadShiftName).joined(separator: ",") }
public func payloadShiftOptionalList() -> [PayloadShift?] { [.other, nil, .plain] }
/// An array holding the hidden case, with a heap-allocated payload: reading the array (and
/// disposing of it) must copy and release the payload, never treat it as a tag byte.
public func payloadShiftListWithNamed() -> [PayloadShift] {
    [.plain, .named(String(repeating: "payload-", count: 4)), .other]
}
public func payloadShiftOptionalNamed() -> PayloadShift? { .named(String(repeating: "payload-", count: 4)) }

/// Optional-typed storage of the hidden-payload enum, read and written as whole Optionals.
public final class PayloadShiftBox {
    public var mode: PayloadShift?
    public init(mode: PayloadShift?) { self.mode = mode }
    public var modeName: String { mode.map(payloadShiftName) ?? "none" }
    public func storeNamed() { mode = .named(String(repeating: "stored-", count: 4)) }
}
