// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Test functions for NativeAOT CustomMarshaller experiments.
// Compiled to a macOS dylib with: swiftc -emit-library -module-name NativeAotSwiftLib
//   -enable-library-evolution -o libNativeAotSwiftLib.dylib NativeAotSwiftLib.swift
//
// Uses @_silgen_name for predictable entry points with Swift calling convention.
// Unlike @_cdecl, @_silgen_name preserves Swift ABI (register layout for Optional, etc.).

// --- Blocker 2: Optional<Int32> parameter ---

// Optional<Int32> layout: 4-byte value + 1-byte discriminator (5 bytes total).
// On ARM64, passed in a single register.
@_silgen_name("nativeaot_accept_optional_int32")
public func acceptOptionalInt32(_ value: Int32?) -> Int32 {
    return value ?? -1
}

// Returns Optional<Int32> — tests marshalling in both directions.
@_silgen_name("nativeaot_double_optional_int32")
public func doubleOptionalInt32(_ value: Int32?) -> Int32? {
    guard let v = value else { return nil }
    return v * 2
}

// --- Blocker 2: Optional<String> parameter (extra-inhabitant type) ---

// Optional<String> layout: 16 bytes (same as String, uses extra inhabitants).
// This is the harder case — no discriminator byte, nil encoded via pointer sentinel.
@_silgen_name("nativeaot_optional_string_length")
public func optionalStringLength(_ value: String?) -> Int32 {
    guard let s = value else { return -1 }
    return Int32(s.count)
}

// --- Blocker 2: String parameter (non-optional, 16 bytes) ---

// String layout: 16 bytes (two words). On ARM64, passed in x0+x1 registers.
@_silgen_name("nativeaot_string_length")
public func stringLength(_ value: String) -> Int32 {
    return Int32(value.count)
}

// String return — tests return-direction marshalling for 16-byte type.
@_silgen_name("nativeaot_string_repeat")
public func stringRepeat(_ value: String, _ count: Int32) -> String {
    return String(repeating: value, count: Int(count))
}

// --- SafeHandle experiment: raw pointer parameter ---

// Takes an UnsafeRawPointer — simulates passing a SafeHandle's IntPtr.
@_silgen_name("nativeaot_read_int32_from_ptr")
public func readInt32FromPtr(_ ptr: UnsafeRawPointer) -> Int32 {
    return ptr.load(as: Int32.self)
}

// Takes an UnsafeMutableRawPointer and writes to it.
@_silgen_name("nativeaot_write_int32_to_ptr")
public func writeInt32ToPtr(_ ptr: UnsafeMutableRawPointer, _ value: Int32) {
    ptr.storeBytes(of: value, as: Int32.self)
}

// --- Baseline: Non-optional blittable types ---

@_silgen_name("nativeaot_add_int32")
public func addInt32(_ a: Int32, _ b: Int32) -> Int32 {
    return a + b
}
