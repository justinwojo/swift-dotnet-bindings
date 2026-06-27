// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nullable-reference-erasure override collision
//
// Complements Collisions/SameModuleOverrideCollision.swift, which exercises the LABEL-based
// collision trigger (process(first:) / process(second:)). This file exercises the OTHER trigger for
// the secondary projected-C# dedup: a non-optional class parameter and an optional class parameter
// erase to the SAME C# nullable reference signature. `transform(_ w: RefBox)` and
// `transform(_ w: RefBox?)` both project to `Transform(RefBox)`, so the overload group is
// disambiguated by a numeric suffix assigned in DECLARATION order — the first-declared overload
// keeps the natural name. Non-optional `transform(_ w: RefBox)` is declared first → `Transform`
// (+100); the optional overload is declared second → `Transform2` (+200).
//
// Scenario A is the hard case: a derived class that overrides ONLY the second (optional) overload —
// i.e. the SUFFIXED slot. Its own class body has a single `transform`, so a naive name recompute
// yields `Transform` and hijacks the base's FIRST (natural-named) slot — a silent
// wrong-vtable-dispatch. The fix makes the override adopt the ancestor slot's emitted name (resolved
// by full Swift selector, param types included), so it correctly emits `override Transform2`. Each
// body returns a distinct offset so dispatch through a BASE-typed reference proves which Swift body
// actually ran.

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
