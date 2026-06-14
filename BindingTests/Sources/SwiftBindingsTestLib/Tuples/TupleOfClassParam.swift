// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Tuple-of-class-element parameter (negative fixture)
//
// A non-frozen public struct projected as a C# class (with `.Payload`) used as an
// element of a tuple parameter. The standard PInvokeEmitter tuple path emits the
// parameter as `ValueTuple<IntPtr, IntPtr>`, but the call site has the raw class
// tuple and no per-element handle extraction — CS1503 at compile time.
//
// The gate must SKIP the broken constructor while keeping the rest of the type
// usable (default ctor + other accessors).

/// Non-frozen reference-shaped value type — projected as a C# class with `.Payload`.
public struct TupleClassElementSize {
    public var width: Int32
    public var height: Int32

    public init(width: Int32, height: Int32) {
        self.width = width
        self.height = height
    }
}

/// Wrapper class whose tuple-parameterized constructor must be skipped by the
/// generator. The default constructor and other accessors must still be reachable
/// from C#.
public class TupleOfClassParamHost {
    public let label: String
    private let storedWidth: Int32
    private let storedHeight: Int32

    public init() {
        self.label = "default"
        self.storedWidth = 0
        self.storedHeight = 0
    }

    /// THIS constructor must be skipped — tuple-of-class parameter has no
    /// per-element marshalling support yet.
    public init(label: String, maxSize: (width: TupleClassElementSize, height: TupleClassElementSize)) {
        self.label = label
        self.storedWidth = maxSize.width.width
        self.storedHeight = maxSize.height.height
    }

    public var width: Int32 { storedWidth }
    public var height: Int32 { storedHeight }
}

// MARK: - Tuple-of-class-element parameter (positive fixture)
//
// Unlike a non-frozen struct (above, projected as a C# `.Payload` class but still
// `TypeRecordKind.Struct`), a *pure Swift class* element has a stable, ABI-faithful
// 8-byte pointer slot. The @_cdecl tuple buffer therefore writes each class element
// as its borrowed (+0) object handle at the element's runtime-metadata offset, and
// the owning ValueTuple is pinned with `GC.KeepAlive` past the native call so the
// backing SafeHandle cannot be finalized — releasing the Swift object — mid-call.
//
// The Swift wrapper reconstructs the tuple with `.assumingMemoryBound(to:).pointee`,
// a typed load that retains each class element for the duration of the call, so the
// borrowed handle survives even though the buffer holds only a +0 reference.

/// Pure Swift reference type — projected as a C# class backed by ARC, an 8-byte
/// pointer in any tuple slot it occupies.
public final class TupleBoxedInt {
    public let value: Int32
    public init(value: Int32) {
        self.value = value
    }
}

/// Tuple of two pure Swift class elements: exercises two pointer-width slots written
/// as borrowed object handles at distinct metadata offsets, with both elements kept
/// alive past the call.
public func sumBoxedPair(_ pair: (TupleBoxedInt, TupleBoxedInt)) -> Int32 {
    return pair.0.value + pair.1.value
}

/// Mixed tuple — a class element plus a trailing primitive. Exercises the buffer's
/// two write modes side by side: the class is written as a borrowed handle (and kept
/// alive), the primitive is written by value (and needs no keep-alive).
public func combineBoxAndScalar(_ mix: (TupleBoxedInt, Int32)) -> Int32 {
    return mix.0.value &+ mix.1
}

// MARK: - Tuple-of-String-element parameter (positive fixture)
//
// A Swift.String element inside a tuple occupies a 16-byte (two-word) frozen-value
// slot — NOT the @_cdecl String-PARAMETER fast path (which lowers to a utf8 ptr+len
// pair). The element is projected as a Swift.SwiftString that owns its storage, so the
// @_cdecl tuple buffer bit-copies that borrowed 16-byte value straight into the slot and
// keeps the owning ValueTuple alive across the call (the same source keep-alive a class
// slot relies on). The Swift wrapper reconstructs the whole tuple with a typed
// `.assumingMemoryBound(to:).pointee` load, which retains each String element for the
// duration of the call.

/// Tuple of two String elements — exercises two 16-byte value slots written as borrowed
/// copies at distinct metadata offsets.
public func joinStringPair(_ pair: (String, String)) -> String {
    return pair.0 + "|" + pair.1
}

/// Mixed tuple spanning all three @_cdecl buffer write modes in one allocation: a
/// 16-byte borrowed String value, a by-value primitive, and a pure Swift class element
/// (borrowed handle). The String and class slots share the source keep-alive; the
/// primitive needs none. All three must round-trip.
public func describeLabeledBox(_ entry: (String, Int32, TupleBoxedInt)) -> String {
    return "\(entry.0)=\(entry.1)+\(entry.2.value)"
}
