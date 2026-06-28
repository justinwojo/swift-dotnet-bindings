// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftUI

// Exercises the pre-gate trailing-default rescue on a CONSTRUCTOR whose reduced
// overload is native-thunk-eligible.
//
// `init(value:edges:)` has an unbindable trailing parameter — `[SwiftUI.Edge]`,
// an array of an unsupported-module type — that carries a `= []` default, so the
// member-emission gate drops the full init and the rescue synthesizes a reduced
// `init(value:)`. That reduced single-Int initializer on a plain class is eligible
// for a native ARM64 thunk.
//
// A native thunk would emit `bl` straight to the full-ABI init symbol, which
// expects BOTH arguments, leaving the `edges` register uninitialized: reading
// `edges.count` then dereferences a garbage array-buffer pointer and faults. The
// rescue is correct ONLY when realized by a @_cdecl wrapper that calls the
// initializer by name so Swift fills `edges = []`. The reduced decl is therefore
// forced onto the @_cdecl path; a correct binding yields `total == value` (edges
// empty → count 0). This is the same shape as the TipKit `init(tip:arrowEdge:)`
// rescue, where `arrowEdge` is a SwiftUI-typed defaulted trailing parameter.
public class GateReducedInitHost {
    public let total: Int

    public init(value: Int, edges: [Edge] = []) {
        self.total = value + edges.count
    }
}
