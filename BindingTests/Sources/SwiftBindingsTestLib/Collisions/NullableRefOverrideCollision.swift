// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nullable-reference-erasure override collision
//
// Complements Collisions/SameModuleOverrideCollision.swift, which exercises the LABEL-based
// collision trigger (process(first:) / process(second:)). This file exercises the OTHER B15
// trigger: a non-optional class parameter and an optional class parameter erase to the SAME C#
// nullable reference signature. `transform(_ w: RefBox)` and `transform(_ w: RefBox?)` both project
// to `Transform(RefBox)`, so the overload group is content-sort disambiguated: the member whose
// full Swift signature sorts first keeps the natural name and the other takes the `…2` suffix.
// The optional overload's signature (`Optional<RefBox>`) sorts ahead of the bare `RefBox`, so the
// OPTIONAL overload is `Transform` (+200) and the NON-OPTIONAL overload is `Transform2` (+100).
//
// The hard case: a derived class that overrides ONLY the non-optional overload — i.e. the SUFFIXED
// slot. Its own class body has a single `transform`, so a naive name recompute yields `Transform`
// and would hijack the base's natural-named (optional) slot — a silent wrong-vtable-dispatch. The
// fix makes the override adopt the ancestor slot's emitted name (resolved by full Swift selector,
// param types included), so it correctly emits `override Transform2`. Each body returns a distinct
// offset so dispatch through a BASE-typed reference proves which Swift body actually ran.

public class RefBox {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

open class NullableRefOverrideBase {
    public init() {}

    /// Non-optional class param. Its signature sorts AFTER the optional's, so it takes the suffixed
    /// `Transform2` slot.
    open func transform(_ w: RefBox) -> Int32 { return w.value + 100 }

    /// Optional class param → erases to the same C# signature. Its signature sorts first, so it
    /// keeps the natural `Transform` slot.
    open func transform(_ w: RefBox?) -> Int32 { return (w?.value ?? 0) + 200 }
}

/// Overrides ONLY the non-optional overload — the SUFFIXED slot. Must adopt the `Transform2` slot,
/// NOT hijack the natural-named `Transform`.
open class NullableRefOverrideDerived: NullableRefOverrideBase {
    public override init() { super.init() }

    open override func transform(_ w: RefBox) -> Int32 { return w.value + 2200 }
}
