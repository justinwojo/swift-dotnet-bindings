// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Apple supplement value-factory helpers
//
// Constructs initialized Foundation.Locale.Language values into a C#-provided
// buffer so cross-assembly round-trip tests can exercise payload copy/destroy
// ABI. C# allocates a buffer of the supplement type's metadata size, calls the
// helper, then wraps the buffer via SwiftObjectHelper<Language>.NewFromPayload
// and destroys the source via VWT.

/// Writes an initialized `Foundation.Locale.Language(identifier: "en")` into
/// `bufferPtr`. The buffer must be at least the size reported by the type's
/// metadata accessor. The caller is responsible for destroying the value with
/// VWT Destroy and freeing the buffer.
@available(iOS 16, tvOS 16, macOS 13, *)
@_cdecl("SBT_AppleSupplement_CreateLocaleLanguage")
public func sbt_appleSupplement_createLocaleLanguage(_ bufferPtr: UnsafeMutableRawPointer) {
    let lang = Foundation.Locale.Language(identifier: "en")
    bufferPtr.initializeMemory(as: Foundation.Locale.Language.self, repeating: lang, count: 1)
}
