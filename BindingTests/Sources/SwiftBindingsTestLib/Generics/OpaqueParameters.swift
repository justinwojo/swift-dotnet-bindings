// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Opaque Parameter Types (fix #6)
//
// Fix #6 (commit 2c80b227) teaches the parser to lower a parameter-position
// `some P` into a synthetic generic parameter. Without the fix, the parser
// encountered an opaque-type node at a parameter slot and crashed or
// dropped the method; the StoreKit direct-mode snapshot work depended on
// this shape compiling cleanly.
//
// The existing `some Named` fixtures in Protocols/NonBlittableProtocols.swift
// cover user-defined protocols at parameter position. This file adds the
// *standard-library* shape that fix #6 also had to handle for StoreKit —
// `some Encodable` — and a user-defined companion (`OpaqueDescribable`)
// that exercises a dedicated single-requirement protocol so the fixture
// doesn't have to rely on Foundation's `Named`-like protocol surface.
//
// The C# test invokes both methods via the generated binding and checks
// pass-through / derived values. Per CLAUDE.md ("assert behavior, not
// implementation details") we do not check the generated C# method
// signature — the test passes if invoking the Swift side returns the
// expected value regardless of whether the generator emits the opaque
// parameter as a constrained generic or as object-fallback.

// MARK: User-defined protocol and conformer

/// Single-requirement protocol used by the user-protocol opaque parameter
/// test below. Kept distinct from existing fixtures' protocols so the test
/// is decoupled from unrelated test-suite evolution.
public protocol OpaqueDescribable {
    var opaqueLabel: String { get }
}

/// Frozen-struct conformer so the binding surface is minimal and the
/// round-trip has no hidden allocation behavior.
public struct OpaqueTag: OpaqueDescribable {
    public let opaqueLabel: String

    public init(label: String) {
        self.opaqueLabel = label
    }
}

// MARK: Fix #6 — User-protocol opaque parameter (character-count probe)

/// Takes `some OpaqueDescribable` at parameter position and returns the
/// Swift character count of the conformer's `opaqueLabel`. Proves the
/// synthetic generic parameter introduced by fix #6 is actually usable
/// inside the method body — not just dropped on the floor after lowering.
public func opaqueLabelCharacterCount(_ value: some OpaqueDescribable) -> Int32 {
    return Int32(value.opaqueLabel.count)
}

// MARK: Fix #6 — Standard-library Encodable opaque parameter (JSON size)

/// Takes `some Encodable` at parameter position and returns the byte count
/// of the conformer's JSON-encoded representation. Exercises the Foundation
/// `Encodable` lowering path that StoreKit's direct-mode snapshot relies on.
/// If the generator's emission for `some Encodable` has regressed and drops
/// this method, the C# call site stops compiling and the regression is
/// caught at `nuke binding-tests` time.
public func opaqueEncodedByteCount(_ value: some Encodable) throws -> Int32 {
    let encoder = JSONEncoder()
    let data = try encoder.encode(value)
    return Int32(data.count)
}
