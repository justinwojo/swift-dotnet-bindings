// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested types named after a Swift metatype keyword
//
// In a type position `MetatypeKeywordHost.`Type`` names the nested struct, but in an expression Swift
// reads `MetatypeKeywordHost.`Type`.self` (backticks or not) as the host's metatype. Every wrapper site
// that names such a type as a value (its metadata accessor, MemoryLayout, initializeMemory(as:),
// assumingMemoryBound(to:)) must reach the nested type, or the binding lays out and copies values
// with the metatype's size.

public final class MetatypeKeywordHost {
    public init() {}

    /// Wider than a metatype (a class metatype is one word), with a reference-counted field. Its
    /// metadata accessor and field accessors bind; a member that names it in a signature does not
    /// yet, because the parsed type spelling drops the back ticks that tell `MetatypeKeywordHost.`Type``
    /// (this struct) from `MetatypeKeywordHost.Type` (the host's metatype), so it is withdrawn rather
    /// than bound against the wrong type.
    public struct `Type` {
        public var first: Int64 = 0
        public var second: Int64 = 0
        public var label: String = ""
    }

    /// A frozen nested struct with the same name problem, laid out inline.
    @frozen public struct `Protocol` {
        public var x: Int32
        public var y: Int32
        public var z: Int32
        public init(x: Int32, y: Int32, z: Int32) { self.x = x; self.y = y; self.z = z }
    }

    public func point(_ seed: Int32) -> `Protocol` { `Protocol`(x: seed, y: seed + 1, z: seed + 2) }
    public func points() -> [`Protocol`] { [point(1), point(10)] }
    public func describePoint(_ value: `Protocol`) -> String { "\(value.x),\(value.y),\(value.z)" }
    public func describePoints(_ values: [`Protocol`]) -> String { values.map(describePoint).joined(separator: ";") }

    public static var typeSize: Int { MemoryLayout<`Type`>.size }
    public static var typeStride: Int { MemoryLayout<`Type`>.stride }
    public static var protocolSize: Int { MemoryLayout<`Protocol`>.size }
}
