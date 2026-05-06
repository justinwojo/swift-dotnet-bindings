// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bundle 05 #2 regression — Conditional Equatable on generic struct
//
// Swift permits a generic type to declare `Equatable` (and `Hashable`)
// **only when the type parameter itself satisfies the conformance**:
//
//     extension Box: Equatable where Item: Equatable {}
//
// The pre-fix C# emit projected this as an unconditional
// `IEquatable<Box<TItem>>` interface plus `Equals(TItem?)`,
// `operator ==`, `operator !=`, and `GetHashCode()` overrides — without
// mirroring the `where Item: Equatable` constraint on `TItem`. The
// resulting C# generic parameter set was a strict superset of the
// Swift-permitted set; consumer code that compiled cleanly against
// `Box<NotEquatable>.Equals(...)` would dispatch at runtime to a Swift
// specialization keyed on `TItem`'s Equatable witness table, which does
// not exist for non-Equatable T. The Swift specialization stub then
// either traps or dereferences a null witness pointer.
//
// The fix is conservative: when the parser cannot prove every generic
// parameter carries a constraint that transitively refines the
// conformance protocol (Equatable / Hashable), the typed equality
// surface is dropped wholesale. Consumers that want value equality on
// such generics fall through to `Equals(object?)` from `System.Object`,
// which boxes-and-compares but never traps. See
// `EquatableConformanceHelper.IsConformanceUnconditionalForCSharp` for
// the predicate.
//
// This fixture is the **smallest reproducer** of the over-broad
// emission. The Swift type below has no constraint on `Item` at the
// type-parameter level; the `extension … : Equatable where Item:
// Equatable` clause is the only place the conformance is gated. The
// matching C# test asserts the typed equality surface is absent on the
// generated `Bundle05CondEqBox<>` class.

/// Generic value-wrapper struct whose `Equatable` conformance is
/// declared conditionally — the canonical shape of the Bundle 05 #2
/// over-broad emission bug.
public struct Bundle05CondEqBox<Item> {
    public let value: Item

    public init(value: Item) {
        self.value = value
    }
}

extension Bundle05CondEqBox: Equatable where Item: Equatable {}

/// Int32-specialized factory so the C# binding always has at least one
/// closed instantiation point that links against the type. Without a
/// reachable factory the generator may skip emission entirely (treating
/// the type as unused), which would defeat the regression test.
public func makeBundle05CondEqBoxInt(_ value: Int32) -> Bundle05CondEqBox<Int32> {
    return Bundle05CondEqBox(value: value)
}
