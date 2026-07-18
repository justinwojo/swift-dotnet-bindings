// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Regression gate for the foreign-nested-enum dangling-metadata-symbol defect.
//
// A nested enum declared inside an extension of a FOREIGN system type
// (Foundation.Data) inherits the foreign module in its SwiftTypeName, and its
// receiver (Data) is absent from this module's public-type set, so the receiver
// is classified module-internal. EnumHandler consequently routes the Swift
// wrapper writer to a discard buffer (the @_cdecl metadata wrapper is never
// emitted). The C# metadata recorder must consult the SAME "can wrapper source
// spell this type?" predicate (enclosing-internal, not the enum's own flag) —
// otherwise it plans a `[DllImport(EntryPoint="SBW_GetMetadata_Foundation_...")]`
// against a wrapper symbol nothing defines, and the wrapper-symbol integrity gate
// (SWIFTBIND108) fail-closes the whole module.
//
// Mirrors ZIPFoundation's `extension Data { enum DataError { ... } }` (a no-raw
// simple enum). The registration was already a swallowed no-op at runtime
// (the enum's metadata accessor never resolves), so skipping it loses nothing.
extension Data {
    /// No-raw simple enum nested in a foreign-receiver extension.
    public enum ForeignNestedMarker {
        case unreadable
        case unwritable
    }

    /// Payload (associated-value) enum nested in the same foreign-receiver
    /// extension — routes through the ISwiftObject metadata path, the sibling
    /// of the simple-enum path above under the same discard writer.
    public enum ForeignNestedPayload {
        case tagged(Int32)
        case empty
    }
}
