// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Keep these C ABI signatures explicit: this fixture is independent of the generator
// and checks runtime marshalling against Swift's own layout and value operations.
@_cdecl("coreclr_canary_add")
public func canaryAdd(_ left: Int32, _ right: Int32) -> Int32 { left + right }

@_cdecl("coreclr_canary_callback")
public func canaryCallback(_ value: Int32, _ context: UnsafeMutableRawPointer?,
    _ callback: @convention(c) (Int32, UnsafeMutableRawPointer?) -> Int32) -> Int32 {
    callback(value + 1, context) + 2
}

@_cdecl("coreclr_canary_array_size")
public func canaryArraySize() -> Int { MemoryLayout<[String]>.size }

@_cdecl("coreclr_canary_array_init")
public func canaryArrayInit(_ output: UnsafeMutableRawPointer) {
    output.initializeMemory(as: [String].self, repeating: ["cold — 🌍", "second"], count: 1)
}

@_cdecl("coreclr_canary_array_destroy")
public func canaryArrayDestroy(_ output: UnsafeMutableRawPointer) {
    output.assumingMemoryBound(to: [String].self).deinitialize(count: 1)
}

@_cdecl("coreclr_canary_array_check")
public func canaryArrayCheck(_ value: UnsafeRawPointer) -> Int32 {
    value.assumingMemoryBound(to: [String].self).pointee == ["cold — 🌍", "second", "managed"] ? 1 : 0
}

@_cdecl("coreclr_canary_dictionary_check")
public func canaryDictionaryCheck(_ value: UnsafeRawPointer) -> Int32 {
    let dictionary = value.assumingMemoryBound(to: [String: Int32].self).pointee
    return dictionary.count == 1 && dictionary["cold-key"] == 73 ? 1 : 0
}

@_cdecl("coreclr_canary_optional_size")
public func canaryOptionalSize() -> Int { MemoryLayout<[Int32]?>.size }

@_cdecl("coreclr_canary_optional_init")
public func canaryOptionalInit(_ output: UnsafeMutableRawPointer) {
    output.initializeMemory(as: Optional<[Int32]>.self, repeating: [17, 29], count: 1)
}

@_cdecl("coreclr_canary_optional_destroy")
public func canaryOptionalDestroy(_ output: UnsafeMutableRawPointer) {
    output.assumingMemoryBound(to: Optional<[Int32]>.self).deinitialize(count: 1)
}

@_cdecl("coreclr_canary_tuple_size")
public func canaryTupleSize() -> Int { MemoryLayout<(Int32, Double, Bool)>.size }

@_cdecl("coreclr_canary_tuple_init")
public func canaryTupleInit(_ output: UnsafeMutableRawPointer) {
    output.initializeMemory(as: (Int32, Double, Bool).self, repeating: (47, 2.5, true), count: 1)
}

@_cdecl("coreclr_canary_tuple_check")
public func canaryTupleCheck(_ value: UnsafeRawPointer) -> Int32 {
    let tuple = value.assumingMemoryBound(to: (Int32, Double, Bool).self).pointee
    return tuple.0 == 89 && tuple.1 == 4.25 && !tuple.2 ? 1 : 0
}
