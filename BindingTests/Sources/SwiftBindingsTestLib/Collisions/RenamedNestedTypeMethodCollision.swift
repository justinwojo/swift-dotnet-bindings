// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - RenamedNestedTypeMethodCollision
//
// A second-order collision that the direct method/nested-type collision fixture
// (`Navigator` in NestedTypeMethodCollision.swift) does NOT exercise.
//
// Here the nested type is first RENAMED by the kind-aware nested-type
// disambiguation pass, and only THEN does a sibling method collide with the
// renamed name:
//
//   struct Ledger {
//     struct Entry { ... }     // nested type; C# leaf would be `Entry`
//     var entry: Entry              // stored property projects to C# `Entry` —
//                                   //   collides with the nested type, so the
//                                   //   nested type is renamed. Entry is a struct
//                                   //   → "Info" suffix → it emits as `EntryInfo`.
//     func entryInfo(scaledBy:) ... // PascalCase name is `EntryInfo` — now collides
//                                   //   with the RENAMED nested type, not the raw
//                                   //   one. (It takes a parameter so it does NOT
//                                   //   pick up the 0-arg getter `Get` prefix, which
//                                   //   would otherwise sidestep the collision.)
//   }
//
// The method-vs-nested-type collision set must reserve the EMITTED leaf name of
// the nested type (`EntryInfo`), not its raw Swift leaf (`Entry`). If it reserves
// the raw name, `entryInfo(scaledBy:)` sails past the collision check and emits as
// `EntryInfo`, colliding with the renamed nested type → CS0102 at compile time.
// Reserving the emitted leaf forces the method to disambiguate (→ `EntryInfoMethod`)
// so both compile.

/// Struct where the nested type is renamed by a property collision and a sibling
/// method's name then collides with the renamed nested type.
public struct Ledger {
    /// Nested type. Its C# leaf `Entry` collides with the `entry` property below,
    /// so the disambiguation pass renames it (struct → "Info") to `EntryInfo`.
    public struct Entry {
        public let amount: Int

        public init(amount: Int) {
            self.amount = amount
        }
    }

    /// Stored property whose C# name `Entry` collides with the nested type,
    /// triggering the nested-type rename to `EntryInfo`.
    public let entry: Entry

    public init(amount: Int) {
        self.entry = Entry(amount: amount)
    }

    /// Instance method whose PascalCase name `EntryInfo` collides with the
    /// RENAMED nested type. It takes a parameter so it does NOT acquire the 0-arg
    /// getter `Get` prefix (which would sidestep the collision), forcing the
    /// generator to disambiguate the method itself.
    public func entryInfo(scaledBy factor: Int) -> Int {
        return entry.amount * factor
    }
}

// MARK: - Helpers exercised by the C# test

/// Builds a Ledger so the C# test can read its `entry` property (the nested type,
/// emitted as `Ledger.EntryInfo`) and call the disambiguated `entryInfo()` method
/// without the test having to name the method's renamed C# form.
public func makeLedger(amount: Int) -> Ledger {
    return Ledger(amount: amount)
}

/// Invokes `Ledger.entryInfo(scaledBy:)` through a free function so the C# test can
/// assert the method still round-trips regardless of the exact renamed C# identifier.
public func invokeLedgerEntryInfo(ledger: Ledger, scaledBy factor: Int) -> Int {
    return ledger.entryInfo(scaledBy: factor)
}
