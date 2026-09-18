// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - A type nested in a bound generic, carried inside an Optional or a tuple
//
// `NbgOptOuter<Int32>.Leaf` reaches the generator as the outer type carrying the generic
// argument with the nested leaf chained under it, so the C# spelling must put the argument
// on the outer segment (`NbgOptOuter<int>.Leaf`). A plain return spells it that way; these
// fixtures wrap the same reference in an Optional and in a tuple, and hand Optionals back
// in as parameters, so the container paths are held to the same spelling and marshal type.
// Each container uses its own outer so the struct and class leaves stay distinct per path.

public struct NbgOptOuter<T> {
    public struct Leaf {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }
    public final class Node {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }
}

public struct NbgTupleOuter<T> {
    public struct Leaf {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }
    public final class Node {
        public let value: Int32
        public init(value: Int32) { self.value = value }
    }
}

public enum NestedBoundGenericContainers {
    public static func optionalLeaf(value: Int32, present: Bool) -> NbgOptOuter<Int32>.Leaf? {
        present ? .init(value: value) : nil
    }
    public static func optionalNode(value: Int32, present: Bool) -> NbgOptOuter<Int32>.Node? {
        present ? .init(value: value) : nil
    }

    public static func leafValue(_ leaf: NbgOptOuter<Int32>.Leaf?) -> Int32 { leaf?.value ?? -1 }
    public static func nodeValue(_ node: NbgOptOuter<Int32>.Node?) -> Int32 { node?.value ?? -1 }

    public static func leafTuple(value: Int32) -> (NbgTupleOuter<Int32>.Leaf, Int32) {
        (.init(value: value), value + 100)
    }
    public static func nodeTuple(value: Int32) -> (NbgTupleOuter<Int32>.Node, Int32) {
        (.init(value: value), value + 200)
    }
}
