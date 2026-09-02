// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// `Set` whose Element is a public, resilient, Hashable STRUCT.
//
// This is the element shape with no per-type `@_cdecl` insert wrapper in the
// runtime: `Set<Int>`, `Set<Int64>` and `Set<String>` each have one, but an
// arbitrary struct element can only go through the Swift stdlib's own generic
// `Set.insert(_:)`. That call's ABI returns
// `(inserted: Bool, memberAfterInsert: Element)` — a mixed tuple where the
// `@out Element` buffer is an ordinary leading pointer argument rather than an
// sret register — which is the shape a Mono CallConvSwift trampoline
// mishandles on the iOS Simulator. So a populated managed set of struct
// elements is the exact input that used to corrupt memory, and these fixtures
// exist to keep that path honest end to end.
//
// Provenance: a third-party consumer constructed a framework view controller
// whose initializer takes a `Set` of a framework-defined Hashable struct, from
// a populated managed set. Nothing here depends on that framework.
//
// The element deliberately mixes a reference-counted `String` with a POD
// `Int32`, so the element's value-witness table has real retain/release work on
// every insert: the incoming element is consumed at +1, and `memberAfterInsert`
// is handed back at +1 for the caller to destroy. A path that leaks or
// over-releases either one surfaces as a drifting count, a crash on dispose, or
// a leak — not merely as a wrong return value. Being non-`@frozen` in a
// library-evolution module also makes it resilient, so its layout is only
// knowable through the value-witness table at runtime.

/// Hashable struct element with a mixed reference/POD payload.
public struct LabeledRank: Hashable {
    public let label: String
    public let rank: Int32

    public init(label: String, rank: Int32) {
        self.label = label
        self.rank = rank
    }
}

/// Member count, reported from the Swift side of the boundary. If insertion
/// corrupted the set's storage slot, this reads garbage rather than the number
/// of elements the caller marshalled.
public func labeledRankSetCount(_ values: Set<LabeledRank>) -> Int32 {
    return Int32(values.count)
}

/// Swift-side membership test. Proves the marshalled members hash and compare
/// equal to an independently marshalled probe — i.e. the payloads crossed
/// intact, not just the count.
public func labeledRankSetContains(_ values: Set<LabeledRank>, _ probe: LabeledRank) -> Bool {
    return values.contains(probe)
}

/// Sum of every member's `rank`. Reads the POD half of each element's payload.
public func labeledRankSetRankSum(_ values: Set<LabeledRank>) -> Int32 {
    return values.reduce(0) { $0 + $1.rank }
}

/// Every member's `label`, sorted and joined. Reads the reference-counted half
/// of each element's payload; a released-too-early string shows up here rather
/// than as a silent zero.
public func labeledRankSetSortedLabels(_ values: Set<LabeledRank>) -> String {
    return values.map(\.label).sorted().joined(separator: ",")
}

/// Swift-built set, for the return direction: `label` is `"item<i>"` and `rank`
/// is `i` for `i` in `0..<count`.
public func makeLabeledRankSet(_ count: Int32) -> Set<LabeledRank> {
    var result: Set<LabeledRank> = []
    for i in 0..<count {
        result.insert(LabeledRank(label: "item\(i)", rank: i))
    }
    return result
}
