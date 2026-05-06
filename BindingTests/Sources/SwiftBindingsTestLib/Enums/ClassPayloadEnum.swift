// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Non-generic enum carrying a concrete Swift class payload. Exercises the
// EnumHandler.Marshalling class-payload deref path: the enum's VWT holds the
// class pointer inline; extraction must dereference `*(IntPtr*)(enumCopy + offset)`
// and `Arc.Retain` for +1 C# ownership. Wrapping the buffer address directly in
// SwiftClassHandle<T> would ARC-release a bogus pointer on dispose (crash).
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

// Reproducer for bug-0.10.0-enum-case-payload-extractor-missing.md.
// Mirrors the Stripe FinancialConnections.Result shape: a Result-style enum
// with a *labeled* class associated value on success, a no-payload cancelation
// case, and a labeled `any Swift.Error` failure case. In the Stripe binding,
// only the `failed(error:)` case received a factory + TryGet — `completed(session:)`
// got just the CaseTag. The bug claim is that the labeled-class-payload case is
// silently skipped while the AnyError-payload case in the same enum still emits.
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
