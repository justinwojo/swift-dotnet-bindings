// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Tuple-of-class-element parameter (negative fixture)
//
// Mirrors the RichTextKit `RichTextImageConfiguration(maxImageSize:)` pattern: a
// non-frozen public struct projected as a C# class (with `.Payload`) used as an
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
