// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the CSM multi-constraint intersection filter
// (`ConcreteSpecializationEngine.ConformerSatisfiesAllConstraints`). Mirrors
// the MusicKit `MusicRecentlyPlayedRequest<T: MusicRecentlyPlayedRequestable, T: Decodable>`
// shape in miniature: a generic struct constrained by a PAT protocol plus a
// secondary marker protocol, where one conformer satisfies both constraints
// and another satisfies only the PAT-selected one.
//
// Why the fixture exists: `FindSpecializableProtocolConstraint` picks ONE
// protocol per generic param (the first PAT/Self requirement, else the first
// protocol with known conformers). When the param carries additional
// constraints, the selected protocol's conformer set is a superset of the
// legal intersection. Without an intersection filter at the pairing step, the
// engine emits CSM overloads for conformers that fail the non-selected
// constraints — the Swift wrapper then fails to compile against a `where`
// clause the conformer cannot satisfy.
//
// The fixture establishes:
//   - PermittedSlot: a PAT (associatedtype Slot) — picked by the engine as
//     the selected constraint via the IsUnsupportedProtocolConstraint path.
//   - Permitted: a marker (Self-requirement-free) — declared on the param
//     as the second constraint, so the filter must intersect against it.
//   - PermittedString / PermittedInt: conform to BOTH protocols (admitted).
//   - SlotOnlyDouble: conforms ONLY to PermittedSlot (must be REJECTED by
//     the filter; without the fix, its CSM extension would emit and break
//     the Swift wrapper compile).
//
// Verification surfaces:
//   1. Generated C# contains `PermittedBagPermittedStringCsmExtensions` and
//      `PermittedBagPermittedIntCsmExtensions` (admitted conformers).
//   2. Generated C# does NOT contain `PermittedBagSlotOnlyDoubleCsmExtensions`
//      (rejected conformer).
//   3. `binding-emission-report.json` `csmConformerRejections` lists a row
//      with `conformer: "SwiftBindingsTestLib.SlotOnlyDouble"`,
//      `selectedProtocol: "SwiftBindingsTestLib.PermittedSlot"`,
//      `missingConstraint: "SwiftBindingsTestLib.Permitted"`.
//   4. Runtime CSM calls on the admitted conformers round-trip values.

public protocol PermittedSlot {
    associatedtype Slot
}

public protocol Permitted {
}

public struct PermittedString: PermittedSlot, Permitted {
    public typealias Slot = String
    public init() {}
}

public struct PermittedInt: PermittedSlot, Permitted {
    public typealias Slot = Int32
    public init() {}
}

public struct SlotOnlyDouble: PermittedSlot {
    public typealias Slot = Double
    public init() {}
}

/// Multi-constraint PAT generic — `<T: PermittedSlot & Permitted>`. The PAT
/// (PermittedSlot) wins the engine's selection. The intersection filter must
/// reject `SlotOnlyDouble` because it does not declare `Permitted`.
public struct PermittedBag<Item: PermittedSlot & Permitted> {
    public var counter: Int32 = 0
    public init() {}

    /// Parent-only sync mutating method. Exercises the CSM extension's
    /// per-conformer dispatch for the admitted conformers.
    public mutating func bump(by amount: Int32) {
        counter &+= amount
    }

    /// Parent-only non-mutating read; witnesses that `bump(by:)` mutated state.
    public func read() -> Int32 {
        return counter
    }
}

// MARK: - Closed-conformer factories
//
// Mirror PatParentPlainProperties / PatParentOnlyMethods: callers obtain
// instances through typed factories so the C# test path does not depend on
// a generic constructor surface. Only the admitted conformers expose
// factories (a factory returning `PermittedBag<SlotOnlyDouble>` would itself
// fail to compile in Swift because SlotOnlyDouble does not satisfy the
// constraint set).

public func makePermittedBagPermittedString() -> PermittedBag<PermittedString> {
    return PermittedBag<PermittedString>()
}

public func makePermittedBagPermittedInt() -> PermittedBag<PermittedInt> {
    return PermittedBag<PermittedInt>()
}
