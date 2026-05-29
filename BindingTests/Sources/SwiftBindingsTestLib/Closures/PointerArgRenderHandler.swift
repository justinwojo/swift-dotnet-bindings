// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Pointer-arg render handler closure (RC-CLOSURE / PlayAudio shape)
//
// Reproduces RealityFoundation's `AudioGenerator` PlayAudio / PrepareAudio render
// handler signature `(UnsafeMutablePointer<AudioBufferList>) -> OSStatus`. The
// closure-parameter gate (`ClosureHandler.IsSupportedClosureParameterType`)
// rejected ANY closure signature naming `OSStatus` because `Darwin.OSStatus`
// resolved to a synthetic ObjC-bridged class record instead of a known primitive,
// so the whole render-handler method was dropped. The fix added `Darwin.OSStatus`
// (and the AVFAudio count aliases) to `MarshallingHelpers.TypeAliasToCSPrimitive`
// / `PrimitiveAliasStrategy`, so the gate now sees `OSStatus` as `Int32`.
//
// `OSStatus` (a `Darwin` typealias for `Int32`) is the part the fix enables — the
// `UnsafeMutablePointer<T>` parameter already passed the gate (every `Unsafe*Pointer<T>`
// is `IntPtr` on the wire regardless of `T`). A plain `Int32` in place of
// `AudioBufferList` keeps the fixture hermetic while still exercising the
// pointer-arg + `OSStatus`-return shape end-to-end.

/// Calls `handler` with a pointer to a mutable `Int32` seeded with `seed` and
/// returns the handler's `OSStatus`. The C# delegate must observe the seed through
/// the pointer and its returned status must propagate back across the boundary.
public func invokeRenderHandler(seed: Int32, handler: (UnsafeMutablePointer<Int32>) -> OSStatus) -> OSStatus {
    var value = seed
    return withUnsafeMutablePointer(to: &value) { handler($0) }
}
