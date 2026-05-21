// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// @_cdecl trampolines that project a small, hand-curated subset of
// Foundation.AttributedString's API into a calling-convention the .NET
// generator can speak. The generator emits a heap-stored partial class
// for the value type but cannot synthesise:
//
//   * any public initializer (AttributedString's only Swift ctors take a
//     Swift String or a Swift Sequence<Character>, neither of which is a
//     blittable @_cdecl-callable shape);
//   * the @dynamicMemberLookup subscript that exposes attribute keys
//     (foregroundColor, languageIdentifier, link, ...) — that subscript
//     is a generic key-path overload over the FoundationAttributeScope
//     hierarchy and has no symbol the binding generator can name.
//
// These shims fill that gap. UTF-8 byte buffers are the lingua franca
// for crossing the boundary — same pattern as SBW_SwiftString_* in
// SwiftBindings.Runtime — so the consumer side never has to dance with
// the non-trivial Swift.String ABI. Allocations returned from `Get*`
// shims are owned by the callee and must be freed via
// SBW_AttributedString_FreeBuffer.
//
// All AttributedString in/out pointers are typed `UnsafeRawPointer` /
// `UnsafeMutableRawPointer` to keep the function signatures
// `@_cdecl`-eligible (typed pointers to non-ObjC-representable Swift
// value types are rejected by the @_cdecl checker). Inside each shim
// the pointer is bound to AttributedString via `.assumingMemoryBound`.
// The C# side is the sole owner of the heap storage, so AttributedString's
// internal copy-on-write semantics see a refcount of 1 and mutate in
// place rather than allocating a new buffer.

import Foundation

/// One-shot constructor: write a fresh `AttributedString(<utf8 string>)`
/// into the caller-provided heap slot. The slot must be the size and
/// alignment of `MemoryLayout<AttributedString>` — the C# side reads that
/// from the type metadata before calling.
@_cdecl("SBW_AttributedString_InitFromUtf8")
public func sbw_attributedStringInitFromUtf8(
    _ utf8Ptr: UnsafePointer<UInt8>?,
    _ utf8Len: Int,
    _ outBuffer: UnsafeMutableRawPointer
) {
    let s: String
    if let utf8Ptr = utf8Ptr, utf8Len > 0 {
        s = String(decoding: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), as: UTF8.self)
    } else {
        s = ""
    }
    outBuffer.assumingMemoryBound(to: AttributedString.self)
        .initialize(to: AttributedString(s))
}

/// Project the plain-text characters of the AttributedString at
/// `astrPtr` to a heap-allocated UTF-8 buffer. `outUtf8Ptr` is set to nil
/// + `outUtf8Len` to 0 when the projection is empty; otherwise the
/// buffer must be freed via SBW_AttributedString_FreeBuffer.
@_cdecl("SBW_AttributedString_GetCharacters")
public func sbw_attributedStringGetCharacters(
    _ astrPtr: UnsafeRawPointer,
    _ outUtf8Ptr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    _ outUtf8Len: UnsafeMutablePointer<Int>
) {
    let str = astrPtr.assumingMemoryBound(to: AttributedString.self).pointee
    let text = String(str.characters)
    writeUtf8Buffer(text, outUtf8Ptr: outUtf8Ptr, outUtf8Len: outUtf8Len)
}

/// Free a UTF-8 buffer returned by any SBW_AttributedString_Get* shim.
/// Safe to call with a nil pointer.
@_cdecl("SBW_AttributedString_FreeBuffer")
public func sbw_attributedStringFreeBuffer(_ ptr: UnsafeMutablePointer<UInt8>?) {
    ptr?.deallocate()
}

// MARK: - @dynamicMemberLookup attribute getters / setters
//
// AttributedString exposes attribute keys through a @dynamicMemberLookup
// subscript over `AttributeDynamicLookup`. The getter returns nil when
// the attribute is not uniformly applied across the whole string; the
// setter applies the attribute to the entire range. languageIdentifier
// is chosen as the canonical example here because its Value type
// (String?) round-trips cleanly through UTF-8 and is Foundation-only
// (no UIKit/AppKit/SwiftUI dependency). Subsequent attribute properties
// follow the same shape.

/// Returns 1 iff a uniform `languageIdentifier` attribute is present on
/// the AttributedString at `astrPtr`. When the result is 1 the UTF-8
/// bytes of the language identifier are written into a fresh buffer
/// pointed at by `outUtf8Ptr` (length in `outUtf8Len`). When the result
/// is 0 the out-parameters are not written and the caller must not read
/// them.
@_cdecl("SBW_AttributedString_GetLanguageIdentifier")
public func sbw_attributedStringGetLanguageIdentifier(
    _ astrPtr: UnsafeRawPointer,
    _ outUtf8Ptr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    _ outUtf8Len: UnsafeMutablePointer<Int>
) -> Int {
    let str = astrPtr.assumingMemoryBound(to: AttributedString.self).pointee
    guard let value = str.languageIdentifier else { return 0 }
    writeUtf8Buffer(value, outUtf8Ptr: outUtf8Ptr, outUtf8Len: outUtf8Len)
    return 1
}

/// Sets `languageIdentifier` on the whole AttributedString at `astrPtr`.
/// `hasValue == 0` removes the attribute (assigns nil); `hasValue == 1`
/// decodes the UTF-8 buffer at `utf8Ptr` and assigns the resulting
/// String. A nil `utf8Ptr` with `hasValue == 1` assigns the empty
/// string.
@_cdecl("SBW_AttributedString_SetLanguageIdentifier")
public func sbw_attributedStringSetLanguageIdentifier(
    _ astrPtr: UnsafeMutableRawPointer,
    _ utf8Ptr: UnsafePointer<UInt8>?,
    _ utf8Len: Int,
    _ hasValue: Int
) {
    let typedPtr = astrPtr.assumingMemoryBound(to: AttributedString.self)
    if hasValue == 0 {
        typedPtr.pointee.languageIdentifier = nil
        return
    }
    if let utf8Ptr = utf8Ptr, utf8Len > 0 {
        typedPtr.pointee.languageIdentifier =
            String(decoding: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), as: UTF8.self)
    } else {
        typedPtr.pointee.languageIdentifier = ""
    }
}

// MARK: - Internal helpers

/// Copy `text`'s UTF-8 bytes into a freshly allocated heap buffer and
/// publish the pointer + length through the supplied out-parameters.
/// Empty input writes (nil, 0) without allocating. The caller owns the
/// returned buffer and must release it via SBW_AttributedString_FreeBuffer.
@inline(__always)
private func writeUtf8Buffer(
    _ text: String,
    outUtf8Ptr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    outUtf8Len: UnsafeMutablePointer<Int>
) {
    let count = text.utf8.count
    if count == 0 {
        outUtf8Ptr.pointee = nil
        outUtf8Len.pointee = 0
        return
    }
    let dest = UnsafeMutablePointer<UInt8>.allocate(capacity: count)
    var i = 0
    for byte in text.utf8 {
        dest[i] = byte
        i += 1
    }
    outUtf8Ptr.pointee = dest
    outUtf8Len.pointee = count
}
