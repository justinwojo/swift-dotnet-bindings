// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nullable-reference-erasure override collision
//
// Complements Collisions/SameModuleOverrideCollision.swift, which exercises the LABEL-based
// collision trigger (process(first:) / process(second:)). This file exercises the OTHER trigger for
// the secondary projected-C# dedup: a non-optional class parameter and an optional class parameter
// erase to the SAME C# nullable reference signature. `transform(_ w: RefBox)` and
// `transform(_ w: RefBox?)` both project to `Transform(RefBox)`. Neither overload carries an
// argument label, so there is nothing to disambiguate them BY label and the group falls through to
// the type rung: each is named from its own SWIFT parameter type — `TransformWithRefBox` (+100) and
// `TransformWithOptionalRefBox` (+200). The Swift types are what distinguish them here; the
// projected C# types are identical by construction, which is why the group collided at all.
//
// Scenario A is the hard case: a derived class that overrides ONLY the optional overload. Its own
// class body has a single, uncontested `transform`, so a naive name recompute yields the bare
// `Transform` and matches NEITHER base slot — a silent wrong-vtable-dispatch. The fix makes the
// override adopt the ancestor slot's emitted name (resolved by full Swift selector, param types
// included), so it correctly emits `override TransformWithOptionalRefBox`. Each body returns a
// distinct offset so dispatch through a BASE-typed reference proves which Swift body actually ran.

public class RefBox {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

open class NullableRefOverrideBase {
    public init() {}

    /// Non-optional class param → `TransformWithRefBox`.
    open func transform(_ w: RefBox) -> Int32 { return w.value + 100 }

    /// Optional class param → erases to the same C# signature → `TransformWithOptionalRefBox`.
    open func transform(_ w: RefBox?) -> Int32 { return (w?.value ?? 0) + 200 }
}

/// Overrides ONLY the optional overload. Must adopt the `TransformWithOptionalRefBox` slot rather
/// than recomputing a bare `Transform` that matches no base slot.
open class NullableRefOverrideDerived: NullableRefOverrideBase {
    public override init() { super.init() }

    open override func transform(_ w: RefBox?) -> Int32 { return (w?.value ?? 0) + 2200 }
}
