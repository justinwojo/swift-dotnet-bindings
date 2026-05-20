// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for CSM admission of `Swift.String` parameters on
// parent-only methods (Session 6, Commit B). Before this work the CSM
// engine's `AreNonGenericParamsCompatible` admission gate — and its
// sibling allowlists on the sync / async method-generic bridges — only
// accepted three `ParamAbiCategory` values: `Primitive`, `ObjCHandle`,
// and `PayloadHandle`. The `Utf8Slice` category (Swift.String marshalled
// as a 2-machine-word `SBW_Utf8Slice`) was rejected, so any parent-only
// method that took a `String` argument fell back to the
// `BoundGenericsHandler` path — the same path that crashes Mono JIT on
// `GenericContainer.count()/tagBytes()`.
//
// `TaggedBag<Item: Tagger>` mirrors `CubbyBag<Item: Cubby>` from
// `PatParentOnlyMethods.swift` (sync) and `AsyncBag<Item: AsyncBagItem>`
// from `PatParentAsyncMethods.swift` (async), but every admission-gated
// instance method now takes at least one `Swift.String` parameter so the
// regression bites at the canonical CSM admission predicate.
//
// `Tagger` is registered in `specialization-hints.json` with both
// `StringTagger` and `IntTagger` so the engine's parent-baseline resolver
// finds non-empty conformer sets and the cross-conformer separation test
// has two closed instantiations to compare.

public protocol Tagger {
    associatedtype Marker
}

public struct StringTagger: Tagger {
    public typealias Marker = String
    public init() {}
}

public struct IntTagger: Tagger {
    public typealias Marker = Int32
    public init() {}
}

public struct TaggedBag<Item: Tagger> {
    public var lastTagLength: Int32 = 0
    public init() {}

    /// Parent-only sync mutating method whose single non-self parameter is a
    /// `Swift.String` — the textbook `Utf8Slice` ABI category. Before the
    /// admission lift, the CSM engine rejected this method outright and it
    /// fell through to the BoundGenericsHandler path.
    public mutating func tag(_ value: String) {
        lastTagLength = Int32(value.count)
    }

    /// Mixed `Utf8Slice` + `Primitive` admission: the canonical "more than
    /// one parameter, of different categories" case. Returns the recomputed
    /// length so the test can witness both the mutation AND the return-path
    /// value in a single call without colliding with the non-mutating
    /// `length()` reader.
    public mutating func tagWithBonus(_ value: String, bonus: Int32) -> Int32 {
        lastTagLength = Int32(value.count) &+ bonus
        return lastTagLength
    }

    /// Parent-only sync read witness — no `String` param itself, but exists so
    /// the C# tests can confirm the prior mutations actually landed in `self_`
    /// (not aliased through some shared payload).
    public func length() -> Int32 {
        return lastTagLength
    }

    /// Parent-only ASYNC method with a `Swift.String` argument. Closes the
    /// same admission gate on the async method-generic bridge path that the
    /// sync methods close on the sync bridge.
    public func measure(_ value: String) async -> Int32 {
        return Int32(value.count)
    }
}

// MARK: - Closed-conformer factories
//
// Mirror `PatParentOnlyMethods.swift`: expose typed factories so the C#
// test path does not depend on a generic constructor surface. The CSM
// extensions emit on `TaggedBag<StringTagger>` and `TaggedBag<IntTagger>` —
// callers obtain instances through these factories.

public func makeTaggedBagStringTagger() -> TaggedBag<StringTagger> {
    return TaggedBag<StringTagger>()
}

public func makeTaggedBagIntTagger() -> TaggedBag<IntTagger> {
    return TaggedBag<IntTagger>()
}
