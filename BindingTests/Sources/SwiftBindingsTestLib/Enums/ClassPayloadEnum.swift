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
