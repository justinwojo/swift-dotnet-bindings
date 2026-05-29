// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - DirectConformanceLocalizedError (Apple-shipping enum shape)
//
// Sibling to SimpleEnumLocalizedError.swift / CaselessNamespaceLocalizedError.swift.
//
// Those fixtures conform to LocalizedError via an *extension*. This fixture
// declares the conformance *directly* on the enum, matching the shape of real
// Apple-shipping enums like `ProximityReader.MobileDocumentReaderError` and
// `FamilyControls.FamilyControlsError`. The direct-declaration shape exercises
// a structurally distinct parser path (the protocol conformance is attached to
// the enum decl itself, not to an extension), which historically diverged from
// the extension-conformance path and made some Apple-framework errors emit a
// C# `[LibraryImport]` whose Swift `@_cdecl` counterpart was missing.

/// Direct-conformance variant: `LocalizedError` is on the enum decl itself,
/// not via an extension. Mirrors `ProximityReader.MobileDocumentReaderError`.
public enum DirectDemoLocalizedError: LocalizedError {
    case missing
    case truncated

    public var errorDescription: String? {
        switch self {
        case .missing:
            return "Direct: missing"
        case .truncated:
            return "Direct: truncated"
        }
    }
}
