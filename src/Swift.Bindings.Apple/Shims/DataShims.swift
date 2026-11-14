// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// @_cdecl construction of Foundation.Data for Swift.Foundation.Data.
//
// Data is two words that swiftc returns directly as { i64, i64 }. Calling
// `Data.init(bytes:count:)` straight from C# makes the .NET runtime lower
// that two-word Swift result itself, which the CoreCLR interpreter does
// not do on x86_64, so Data could not be built there at all. Through this
// shim the transition is plain C: a pointer, a count and the caller's
// two-word slot, which every runtime places identically, and Swift writes
// the value into the slot at +1, exactly as the initializer returned it.

import Foundation

/// Writes `Data(bytes: bytes, count: count)` into `result`, which must be
/// the size and alignment of `MemoryLayout<Data>` (two words). A null
/// `bytes` (an empty managed array has no address) writes an empty Data.
@_cdecl("SBW_Data_InitWithBytes")
public func sbw_dataInitWithBytes(
    _ bytes: UnsafeRawPointer?,
    _ count: Int,
    _ result: UnsafeMutableRawPointer
) {
    let data = bytes.map { Data(bytes: $0, count: count) } ?? Data()
    result.initializeMemory(as: Data.self, repeating: data, count: 1)
}
