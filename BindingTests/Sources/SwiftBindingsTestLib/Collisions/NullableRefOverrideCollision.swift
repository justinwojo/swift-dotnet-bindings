// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nullable-reference-erasure override collision (P1-21 Scenario A)
//
// Complements Collisions/SameModuleOverrideCollision.swift, which exercises the LABEL-based
// collision trigger (process(first:) / process(second:)). This file exercises the OTHER B15
// trigger: a non-optional class parameter and an optional class parameter erase to the SAME C#
// nullable reference signature. `transform(_ w: RefBox)` and `transform(_ w: RefBox?)` both project
// to `Transform(RefBox)`, so B15 disambiguates them as `Transform` (+100) and `Transform2` (+200).
//
// Scenario A is the hard case: a derived class that overrides ONLY the second (optional) overload.
// Its own class body has a single `transform`, so a naive name recompute yields `Transform` and
// hijacks the base's FIRST slot — a silent wrong-vtable-dispatch. The fix makes the override adopt
// the ancestor slot's emitted name (resolved by full Swift selector, param types included), so it
// correctly emits `override Transform2`. Each body returns a distinct offset so dispatch through a
// BASE-typed reference proves which Swift body actually ran.

public class RefBox {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

open class NullableRefOverrideBase {
    public init() {}

    /// Non-optional class param → first slot.
    open func transform(_ w: RefBox) -> Int32 { return w.value + 100 }

    /// Optional class param → erases to the same C# signature → second slot (`Transform2`).
    open func transform(_ w: RefBox?) -> Int32 { return (w?.value ?? 0) + 200 }
}

/// Overrides ONLY the optional overload. Must adopt the `Transform2` slot, NOT hijack `Transform`.
open class NullableRefOverrideDerived: NullableRefOverrideBase {
    public override init() { super.init() }

    open override func transform(_ w: RefBox?) -> Int32 { return (w?.value ?? 0) + 2200 }
}
