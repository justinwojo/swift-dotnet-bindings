// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A nested type named like its container
//
// Swift accepts `Outer.Outer` (and `Outer.outer`); C# forbids a member named like its enclosing
// type (CS0542), and every container that holds a nested type projects as a C# class. The nested
// type takes the kind-aware suffix (`OuterInfo` for a struct or class, `OuterKind` for an enum)
// and the container keeps its name. The container's own members yield to the renamed type, so the
// name stays the same in every SDK copy whatever members a newer one adds.

/// A struct with stored members (a C# class) holding a lowercase class of its own name, and a
/// property that projects onto the renamed leaf, which yields to the type.
public struct NestedNameOuter {
    public var value: Int32
    public var nestedNameOuterInfo: Int32

    public init(value: Int32) {
        self.value = value
        self.nestedNameOuterInfo = value + 1
    }

    public final class nestedNameOuter {
        public init() {}
        public func ping() -> Int32 { return 41 }
    }
}

/// A member-less struct: a namespace facade, emitted as a static class.
public struct NestedNameFacade {
    public struct NestedNameFacade {
        public var a: Int32
        public init(a: Int32) { self.a = a }
    }
}

/// A caseless enum (a static class) holding an enum of its own name.
public enum NestedNameSpace {
    public enum nestedNameSpace: Int32 {
        case one = 1
        case two = 2
    }

    public static func describe(_ kind: nestedNameSpace) -> Int32 {
        return kind.rawValue * 10
    }

    /// Projects onto the renamed enum's leaf, so it yields to the type.
    public static func nestedNameSpaceKind(_ input: Int32) -> Int32 {
        return input + 7
    }
}

/// A class holding an enum of its own name.
public final class NestedNameBox {
    public init() {}

    public enum NestedNameBox {
        case first
        case second
    }

    public func make() -> NestedNameBox {
        return .second
    }
}

/// An enum with a payload (a C# class) holding a struct of its own name, and a case of its own
/// name, whose factory may not take the enum's name either.
public enum NestedNamePayload {
    case value(Int32)
    case empty
    case nestedNamePayload(Int32)

    public struct NestedNamePayload {
        public var tag: Int32
        public init(tag: Int32) { self.tag = tag }
    }

    public var tagged: NestedNamePayload {
        switch self {
        case .value(let v): return NestedNamePayload(tag: v)
        case .empty: return NestedNamePayload(tag: -1)
        case .nestedNamePayload(let v): return NestedNamePayload(tag: v * 2)
        }
    }
}
