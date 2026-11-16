// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A stored property named like a type parameter of its own type. `t` PascalCases to `T`, the name
// of the C# type parameter, and C# rejects a member named like one (CS0102), so the property takes
// the member-value suffix (`TValue`). A struct and a class carry the same shape; each also has a
// property whose name does not collide, which keeps its name.

public struct GenericParameterNamedField<T> {
    public var t: T
    public var tag: Int32

    public init(t: T, tag: Int32) {
        self.t = t
        self.tag = tag
    }

    public func describe() -> String { "field:\(t):\(tag)" }
}

public final class GenericParameterNamedCell<T> {
    public var t: T

    public init(_ t: T) { self.t = t }

    public func describe() -> String { "cell:\(t)" }
}

/// The suffixed name is already a sibling's: `tValue` keeps `TValue`, and `t` moves past it to
/// `TValue2` rather than both emitting as `TValue`.
public struct GenericParameterNamedNeighbour<T> {
    public var t: T
    public var tValue: Int32

    public init(t: T, tValue: Int32) {
        self.t = t
        self.tValue = tValue
    }

    public func describe() -> String { "neighbour:\(t):\(tValue)" }
}
