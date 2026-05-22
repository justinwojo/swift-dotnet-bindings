// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Mirrors AppIntents.IntentCollectionSize.init(min:max:) / init(exactly:):
// the `_const` (compile-time-constant) parameter modifier requires a literal
// at the call site. The generator must filter `@_cdecl` wrapper emission for
// these inits because the wrapper passes a runtime variable through, which the
// Swift compiler rejects (G51777C7D in AppIntents 0.12.0 cdecl wrappers).
//
// Two const inits plus a regular (non-const) init exercise both branches of
// the filter. The regular init takes a distinct signature so it doesn't dedup
// against `init(exactly:)`.
@frozen
public struct ConstLiteralBox {
    public let lo: Int
    public let hi: Int

    /// Const-literal init — must NOT have a @_cdecl wrapper emitted.
    public init(lo: _const Int, hi: _const Int) {
        self.lo = lo
        self.hi = hi
    }

    /// Const-literal exactly-init — must NOT have a @_cdecl wrapper emitted.
    public init(exactly: _const Int) {
        self.lo = exactly
        self.hi = exactly
    }

    /// Regular non-const init — MUST get a @_cdecl wrapper.
    public init(low: Int32, high: Int32) {
        self.lo = Int(low)
        self.hi = Int(high)
    }
}
