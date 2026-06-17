// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// R6-1 regression fixture (audit Regression-R6, finding #1): the generated
// wrapper post-processor (`SwiftWrapperPostProcessor.ReferencesInternalType`)
// used to strip any `@_cdecl`/`@_silgen_name` block whose body matched the bare
// short name of ANY internal type with `\b<name>\b`. Because `.` is a regex word
// boundary, a current-module internal type named `Data` made `\bData\b` match the
// `.Data` suffix of a qualified cross-module reference like `Foundation.Data`,
// deleting a correct public wrapper and suppressing its C# P/Invoke (silent
// public-API loss / EntryPointNotFound). The module-aware emission gate
// (`InternalTypeReferenceWalker`) kept the member; the naive text matcher wrongly
// stripped it.
//
// This fixture puts the short name `Data` into the generator's internal-type set
// via an `@usableFromInline internal` type (plain `internal` would not reach the
// ABI / symbol graph, so the collision would never form) and then exposes a
// PUBLIC function returning `Foundation.Data`. The collision type is NESTED under
// a public namespace ON PURPOSE: a module-level `internal struct Data` would
// shadow the unqualified `Data` (== `Foundation.Data`) that other sources in this
// module rely on (e.g. `SigningSpecialization`, `ConstructorParams`), breaking the
// Swift build independently of the binding bug.
//
// The nested placement is also the load-bearing half of the fix. A nested internal
// type is unreachable from the generated wrapper via its bare leaf name (Swift
// requires `ShortNameCollisionFixture.Data` / `…Module….Data` at the wrapper's
// top-level scope), so `Program.CollectInternalTypeNames` no longer contributes the
// bare short name `Data` for it — only the qualified forms. That stops `Data` from
// matching BOTH the `.Data` suffix of a qualified `Foundation.Data` return AND a
// BARE `Data` parameter (e.g. the `_dbw_append(_ data: Data)` shims emitted for
// hashers), the latter of which the module-aware matcher alone cannot distinguish
// from the internal type. The runtime invariant is that `makeCollisionData()` still
// binds and round-trips its bytes, and that unrelated `Data`-taking wrappers survive.

/// Public namespace housing the internal collision type so it does not shadow the
/// module-level `Data` other sources use to mean `Foundation.Data`.
public enum ShortNameCollisionFixture {
    /// `@usableFromInline internal` so it reaches the ABI as a nested internal
    /// type whose leaf name is `Data` — the exact short-name collision with
    /// `Foundation.Data` that drove R6-1. Post-fix the internal-type collector
    /// contributes only this type's QUALIFIED forms, never the bare leaf `Data`
    /// (see the matcher note above and the `...ContributesNoBareShortName` unit test).
    @usableFromInline
    internal struct Data {
        @usableFromInline
        internal var marker: Int32

        @usableFromInline
        internal init(marker: Int32) {
            self.marker = marker
        }
    }

    /// Keeps the internal collision type referenced (not dead-stripped by swiftc)
    /// without exposing it on the public surface.
    @usableFromInline
    internal static func internalMarker() -> Int32 {
        return Data(marker: 7).marker
    }
}

/// Public API that surfaces the cross-module `Foundation.Data`. Its generated
/// `@_cdecl` wrapper references `Foundation.Data` QUALIFIED; the internal short
/// name `Data` must NOT cause the post-processor to strip this block. Returns a
/// fixed 4-byte payload so the C# round-trip assertion needs no parameter.
public func makeCollisionData() -> Foundation.Data {
    return Foundation.Data([0x2A, 0x2B, 0x2C, 0x2D])
}
