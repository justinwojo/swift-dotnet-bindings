// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Non-generic enum carrying a concrete Swift class payload. Exercises the
// EnumHandler.Marshalling class-payload deref path: the enum's VWT holds the
// class pointer inline; extraction must dereference `*(IntPtr*)(enumCopy + offset)`
// and `Arc.UnknownObjectRetain` (isa-dispatch — swift_retain for a pure-Swift
// payload, objc_retain for an @objc:NSObject one) for +1 C# ownership. Wrapping
// the buffer address directly in SwiftClassHandle<T> would ARC-release a bogus
// pointer on dispose (crash). See the @objc variants below (issue #40).
//
// Distinct from `Holder<IntBox>` — that goes through the bare-generic-parameter
// path (τ_0_0 → C# type parameter). A non-generic enum with a named class
// payload instead routes through `EmitPayloadMarshal` → `IsSwiftClassPayload`.

/// Minimal class fixture used as an enum payload.
public class BoxedCounter {
    public let count: Int32
    public init(count: Int32) {
        self.count = count
    }
}

/// Non-generic enum with a concrete class associated value (single payload)
/// plus a no-payload case.
public enum ClassOutcome {
    case delivered(BoxedCounter)
    case dropped
}

public func makeDeliveredOutcome(_ count: Int32) -> ClassOutcome {
    return .delivered(BoxedCounter(count: count))
}

public func makeDroppedOutcome() -> ClassOutcome {
    return .dropped
}

/// Non-generic enum with a tuple payload containing a concrete class element.
/// Exercises `EmitPayloadMarshalWithOffset` (per-element tuple extraction).
public enum TaggedDelivery {
    case shipped((Int32, BoxedCounter))
    case pending
}

public func makeShippedDelivery(tag: Int32, count: Int32) -> TaggedDelivery {
    return .shipped((tag, BoxedCounter(count: count)))
}

public func makePendingDelivery() -> TaggedDelivery {
    return .pending
}

// Reproducer for the missing enum-case payload extractor bug.
// A Result-style enum with a *labeled* class associated value on success, a
// no-payload cancelation case, and a labeled `any Swift.Error` failure case.
// The bug was that the labeled-class-payload case received only a CaseTag —
// no factory + TryGet — while the AnyError-payload case in the same enum still emitted.
//
// Exists alongside `ClassOutcome.delivered(BoxedCounter)` (unlabeled, single class
// payload) which already emits factory + TryGet — so any divergence between this
// fixture and ClassOutcome is the regression surface.

/// Minimal `Codable`-free reference type used as the labeled success payload.
public class LabeledFCSession {
    public let id: String
    public init(id: String) {
        self.id = id
    }
}

/// Result-style enum with a labeled class success payload, a no-payload
/// cancelation case, and a labeled `any Swift.Error` failure case.
public enum LabeledClassResult {
    case completed(session: LabeledFCSession)
    case canceled
    case failed(error: any Swift.Error)
}

public struct LabeledResultError: Swift.Error {
    public let message: String
    public init(message: String) {
        self.message = message
    }
}

public func makeLabeledCompletedResult(_ id: String) -> LabeledClassResult {
    return .completed(session: LabeledFCSession(id: id))
}

public func makeLabeledCanceledResult() -> LabeledClassResult {
    return .canceled
}

public func makeLabeledFailedResult(_ message: String) -> LabeledClassResult {
    return .failed(error: LabeledResultError(message: message))
}

// MARK: - @objc:NSObject class payload (issue #40 — enum direction)
//
// The pure-Swift fixtures above route the same EnumHandler.Marshalling class-payload
// extraction, but for them swift_retain and swift_unknownObjectRetain are
// indistinguishable. These variants carry an `@objc … : NSObject` payload, where a
// native-only swift_retain touches the wrong refcount word — the C# wrapper then
// objc_releases on dispose, underflowing the object's true ARC count. The extraction
// MUST use the isa-dispatching Arc.UnknownObjectRetain.
//
// Reuses `ObjCClassParamPayload` (Protocols/ClassParamCallback.swift), which feeds the
// shared LifetimeTracker counters, so the C# side asserts ARC *balance* (live count
// returns to 0 after dispose), not merely the absence of a crash.

/// Non-generic enum carrying a concrete @objc:NSObject class payload. Routes through
/// `EmitPayloadMarshal` → `IsSwiftClassPayload` (which does NOT exclude ObjCRooted) —
/// the E2 site.
public enum ObjCClassOutcome {
    case delivered(ObjCClassParamPayload)
    case dropped
}

public func makeObjCDeliveredOutcome(code: Int32, label: String) -> ObjCClassOutcome {
    return .delivered(ObjCClassParamPayload(code: code, label: label))
}

public func makeObjCDroppedOutcome() -> ObjCClassOutcome {
    return .dropped
}

/// Enum with a tuple payload containing an @objc:NSObject class element. Exercises
/// `EmitPayloadMarshalWithOffset` (per-element tuple extraction at a computed offset) —
/// the E1 site.
public enum ObjCTaggedDelivery {
    case shipped((Int32, ObjCClassParamPayload))
    case pending
}

public func makeObjCShippedDelivery(tag: Int32, code: Int32, label: String) -> ObjCTaggedDelivery {
    return .shipped((tag, ObjCClassParamPayload(code: code, label: label)))
}

public func makeObjCPendingDelivery() -> ObjCTaggedDelivery {
    return .pending
}
