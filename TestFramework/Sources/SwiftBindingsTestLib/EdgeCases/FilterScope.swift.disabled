// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum Cases with Keyword Labels (S1 pattern from Alamofire)

/// Enum with associated values using C# keyword argument labels.
/// Tests that the generator correctly escapes labels like "in", "for",
/// "operator", and "class" in enum case factory functions and wrapper methods.
///
/// Currently disabled (.disabled extension) because the generator produces
/// invalid C# compound identifiers like `__@in` and `__@for` — the `@` prefix
/// must be at the start of an identifier in C#, not after other characters.
/// See: known-issues-workarounds.md (S1 pattern).
///
/// To enable: rename to FilterScope.swift (remove .disabled extension).
/// The .disabled extension is the only gate — no Package.swift changes needed.
public enum FilterScope {
    case include(`in`: String)
    case exclude(`for`: String)
    case custom(`operator`: String, `class`: String)
}
