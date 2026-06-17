// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Fail-closed regression fixture for the parent-CSM METHOD-level stricter-constraint filter
// (`ConcreteSpecializationEngine.ParentTupleSatisfiesMethodConstraints`, F20). Companion to
// PatBagConformerMismatch.swift, which covers the TYPE-level intersection filter
// (`ConformerSatisfiesAllConstraints`); this fixture covers the sibling METHOD-level path — the
// one the type-level filter cannot reach.
//
// Shape — mirrors the engine doc-comment's real example
// `MusicCatalogResourceRequest.init() where MusicItemType : MusicCatalogTopLevelResourceRequesting`:
// a parent-only CSM struct `RefinableBag<Item: RefinableSlot>` is specialized per conformer of
// the PAT-selected parent constraint. One method carries a where-clause that adds a SECOND
// protocol (`RefinableMark`) the parent type does not require. A conformer that satisfies the
// parent constraint but NOT the method's stricter constraint must have THAT METHOD dropped from
// its specialization — the type still specializes; only the over-constrained method is filtered.
//
// Why this is distinct from PatBagConformerMismatch: there the whole TYPE is rejected because the
// conformer fails a parent-LEVEL constraint. Here `SlotOnlyItem` satisfies the parent constraint
// (`RefinableSlot`), so `RefinableBag<SlotOnlyItem>` is a legal, admitted specialization — the
// type-level filter never fires. Only the per-method filter can drop `refinedLabel()`. Without
// it the emitter writes `RefinableBag<SlotOnlyItem>().refinedLabel()` against the
// `where Item: RefinableMark` clause `SlotOnlyItem` cannot satisfy → hard Swift wrapper compile
// error.
//
// The fixture establishes:
//   - RefinableSlot: a PAT (associatedtype Slot) — selected as the parent constraint via the
//     IsUnsupportedProtocolConstraint path, so RefinableBag is CSM-specialized per conformer.
//   - RefinableMark: a marker — added only on the method-level `where`, so the per-method filter
//     must intersect against it.
//   - FullyRefinedItem: conforms to BOTH (refinedLabel() survives for its specialization).
//   - SlotOnlyItem: conforms ONLY to RefinableSlot (refinedLabel() must be DROPPED for its
//     specialization; bump(by:)/read() still emitted).
//
// Verification surfaces (the matching C# round-trip lives in
// ParentMethodStricterConstraintTests; the presence of those tests requires the Swift wrapper to
// compile, which requires the engine's per-method filter to drop SlotOnlyItem's refinedLabel()):
//   1. Generated C# exposes refinedLabel() on the FullyRefinedItem specialization.
//   2. Generated C# does NOT expose refinedLabel() on the SlotOnlyItem specialization.
//   3. binding-emission-report.json records a method-where rejection for SlotOnlyItem against
//      RefinableMark.
//   4. Both specializations round-trip the parent-only bump(by:)/read().

public protocol RefinableSlot {
    associatedtype Slot
}

public protocol RefinableMark {
}

public struct FullyRefinedItem: RefinableSlot, RefinableMark {
    public typealias Slot = String
    public init() {}
}

public struct SlotOnlyItem: RefinableSlot {
    public typealias Slot = Int32
    public init() {}
}

/// Parent-only CSM struct — `<Item: RefinableSlot>` (PAT). Specialized per conformer.
public struct RefinableBag<Item: RefinableSlot> {
    public var counter: Int32 = 0
    public init() {}

    /// Parent-only sync mutating method — emitted for EVERY RefinableSlot conformer.
    public mutating func bump(by amount: Int32) {
        counter &+= amount
    }

    /// Parent-only non-mutating read — witnesses that bump(by:) mutated state.
    public func read() -> Int32 {
        return counter
    }

    /// Method-level STRICTER constraint: valid only when Item ALSO conforms to RefinableMark.
    /// Must be DROPPED from SlotOnlyItem's specialization (SlotOnlyItem lacks RefinableMark).
    public func refinedLabel() -> String where Item: RefinableMark {
        return "refined#\(counter)"
    }
}

// MARK: - Closed-conformer factories
//
// BOTH conformers expose a factory (unlike PatBagConformerMismatch, where the rejected conformer
// has none): SlotOnlyItem's TYPE is legal (it conforms to the parent RefinableSlot), so
// `RefinableBag<SlotOnlyItem>()` compiles — it is only refinedLabel() that must be filtered out.

public func makeFullyRefinedBag() -> RefinableBag<FullyRefinedItem> {
    return RefinableBag<FullyRefinedItem>()
}

public func makeSlotOnlyBag() -> RefinableBag<SlotOnlyItem> {
    return RefinableBag<SlotOnlyItem>()
}
