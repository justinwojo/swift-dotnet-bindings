// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Apple Supplement probe — minimal @_cdecl symbol whose presence in the
// `SwiftBindingsAppleSupplement.xcframework` proves three things end-to-end:
//
//   1. `nuke build-apple-supplement-xcframework` produced a multi-slice
//      xcframework with @rpath/SwiftBindingsAppleSupplement.framework/...
//      install names.
//   2. The framework is reachable via `SwiftFrameworkResolver`'s
//      `@rpath/{name}.framework/{name}` search path at consumer runtime.
//   3. The `[LibraryImport("SwiftBindingsAppleSupplement", ...)]` resolver
//      wiring is live for assemblies that depend on the Apple supplement.
//
// Subsequent shim files (AttributedStringAttributes.swift, etc.) layer on
// top of this same target framework.

import Foundation

/// Returns its input + 1. Trivial round-trip used by BindingTests'
/// AppleSupplementSmokeTests to verify the supplement framework loads
/// and dispatches.
@_cdecl("SBW_AppleSupplement_Probe_AddOne")
public func SBW_AppleSupplement_Probe_AddOne(_ value: Int) -> Int {
    return value &+ 1
}
