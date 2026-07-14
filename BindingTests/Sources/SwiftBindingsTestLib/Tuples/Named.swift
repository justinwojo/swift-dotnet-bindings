// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Named Tuple Functions

/// Returns a named 2-element tuple.
public func makeNamedPair() -> (x: Int32, y: Int32) {
    return (x: 10, y: 20)
}

/// Returns a named 3-element tuple.
public func makeNamedTriple() -> (x: Int32, y: Int32, z: Int32) {
    return (x: 1, y: 2, z: 3)
}

/// Accepts a named tuple and processes it.
public func processNamed(_ point: (x: Int32, y: Int32)) -> Int32 {
    return point.x + point.y
}

/// Returns a named tuple with mixed types.
///
/// A String element in a named-tuple RETURN occupies a 16-byte (two-word) inline value
/// slot in the @_cdecl indirect-result buffer — the same wire as its positional analog.
/// The element label affects only the C# tuple's public field names, never the marshalling.
public func makeNamedMixed() -> (name: String, age: Int32, active: Bool) {
    return (name: "Test", age: 25, active: true)
}

/// Returns a named tuple whose two leading elements are both 16-byte buffer-backed value types
/// (String and Foundation.Data). Sibling of makeNamedMixed for the second buffer-backed element
/// family: both slots are read as the address of their inline value at the element's ABI offset.
public func makeNamedWithData() -> (label: String, payload: Data, count: Int32) {
    let bytes = Data([0x01, 0x02, 0x03, 0x04])
    return (label: "blob", payload: bytes, count: Int32(bytes.count))
}
