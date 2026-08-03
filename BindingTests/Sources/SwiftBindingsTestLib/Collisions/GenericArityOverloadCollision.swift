// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A method-generic overload and a non-generic namesake that DIRECTLY break protocol conformance
//
// A method-generic overload and a non-generic one whose parameters project to the SAME C# parameter
// key are still legal, distinct C# overloads — method-level generic arity is part of overload
// identity. The projected-overload key the secondary dedup uses must encode that arity. An arity-blind
// key collision-groups the two, and the group is then renamed off the bare name — neither sibling
// carries a label, so both escalate to the type rung (`TransformWithRefBox` / `TransformWithOptionalRefBox`).
// When the non-generic shape is also a protocol requirement declared under the bare name, the renamed
// non-generic concrete member no longer satisfies the interface → CS0535 at binding-compile time. A real event-monitor type
// broke this way: a non-generic `request(_:didParseResponse:)` taking a concrete `Response<Data?>`
// alongside a generic `request<Value>(_:didParseResponse:)` over `Response<Value>`.
//
// Reproducing the compile-time CS0535 needs the conformer emitted as a DIRECT `class : IInterface`,
// and that requires decoupling two things that otherwise both key off declaration order:
//   1. the conformance validator's requirement→witness match (keys off the RAW Swift parameter type), and
//   2. the secondary projected-key collision rank (keys off the PROJECTED C# parameter type + decl order).
// If the generic's RAW parameter type equaled the requirement's, the validator would match the generic
// (declared first), find it un-emittable, and silently DROP the whole conformance — no `: IInterface`,
// no CS0535. The lever that splits them: a non-optional class parameter and an optional one ERASE to
// the same C# nullable-reference signature (`RefBox` and `RefBox?` both project to `Transform(RefBox)`)
// yet carry DISTINCT raw Swift types. So:
//   - The generic `transform<Tag>(_ box: RefBox?)` has raw key `transform(RefBox?)`, which does NOT match
//     the requirement's `transform(RefBox)` — the validator skips it and matches the non-generic witness,
//     which IS emittable, so the conformance is accepted and `: IRefBoxArityTransform` is emitted.
//   - Both project to the bare `Transform(RefBox)` overload key, so without the arity marker the generic and the
//     non-generic STILL contend for the same slot. The group is then disambiguated and the non-generic
//     requirement-satisfier is renamed off `Transform` — and because the validator's name-parity check does not
//     model that rename, the class is still declared `: IRefBoxArityTransform` while no member is named
//     `Transform` → the interface's `Transform(RefBox)` is unimplemented → CS0535.
//   - The arity marker (`` `1``) on the generic's projected key keeps the two apart so the non-generic
//     keeps the bare slot and the conformance compiles.
// The generic parameter `Tag` appears ONLY in the return position, which neither requirement matching nor
// overload identity considers — it exists solely to make the method generic so the arity marker applies.
//
// This is primarily a GENERATION-TIME guard: a regression that re-blinds the projected key makes the
// generated binding FAIL TO COMPILE (CS0535 on `IRefBoxArityTransform.Transform`). The runtime test also
// confirms the bare slot dispatches to the non-generic body, both on the concrete receiver and through
// the interface.
//
// `RefBox` (the nullable-erasure lever) is the public class declared in Collisions/NullableRefOverrideCollision.swift.

public protocol RefBoxArityTransform {
    // Non-generic requirement, declared bare → the concrete must keep a bare `Transform(RefBox)`.
    func transform(_ box: RefBox) -> Int32
}

public final class RefBoxArityTransformer: RefBoxArityTransform {
    public init() {}

    // Method-generic, declared FIRST. Its `RefBox?` parameter ERASES to the same projected key as the
    // non-generic `RefBox` (nullable reference annotation does not distinguish C# overloads), but its RAW
    // Swift type `RefBox?` differs, so the conformance validator skips it and matches the non-generic
    // witness below. Without the arity marker this (declared first) claims the bare `Transform` slot.
    // `Tag` is used only in the return position so the method is generic without affecting param identity.
    public func transform<Tag>(_ box: RefBox?) -> Tag? { return nil }

    // Non-generic → must keep the bare `Transform(RefBox)` slot and satisfy the protocol requirement.
    public func transform(_ box: RefBox) -> Int32 { return box.value + 50 }
}
