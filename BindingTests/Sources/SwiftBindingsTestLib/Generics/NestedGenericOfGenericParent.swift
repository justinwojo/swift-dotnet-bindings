// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A generic struct nested inside a generic struct: `SourceNamedShelf<Value>.Tagged<Element>`.
// The nested type's generic signature carries BOTH levels (`<Value, Element>`), so its
// metadata accessor takes the parent's argument and then its own, followed by the metadata
// request — `Tagged<String>` on its own names no type. Members of the nested types reach
// both parameters: a stored property of each, an initializer, and a method returning each,
// on a struct (`Tagged`) and on a class (`Ledger`).

import Foundation

public struct SourceNamedShelf<Value> {
    public let label: Value

    public init(label: Value) {
        self.label = label
    }

    public struct Tagged<Element> {
        public let tag: Value
        public var element: Element

        public init(tag: Value, element: Element) {
            self.tag = tag
            self.element = element
        }

        public var summary: String {
            return "\(tag):\(element)"
        }

        public func currentElement() -> Element {
            return element
        }

        public func currentTag() -> Value {
            return tag
        }
    }

    public class Ledger<Element> {
        public let tag: Value
        public var element: Element

        public init(tag: Value, element: Element) {
            self.tag = tag
            self.element = element
        }

        public var summary: String {
            return "\(tag)=\(element)"
        }

        public func currentTag() -> Value {
            return tag
        }

        public func currentElement() -> Element {
            return element
        }
    }
}

public func makeShelfTaggedIntString(_ tag: Int, _ element: String) -> SourceNamedShelf<Int>.Tagged<String> {
    return SourceNamedShelf<Int>.Tagged(tag: tag, element: element)
}

public func makeShelfTaggedStringInt(_ tag: String, _ element: Int) -> SourceNamedShelf<String>.Tagged<Int> {
    return SourceNamedShelf<String>.Tagged(tag: tag, element: element)
}

public func describeShelfTaggedIntString(_ value: SourceNamedShelf<Int>.Tagged<String>) -> String {
    return value.summary
}

public func makeShelfLedgerIntString(_ tag: Int, _ element: String) -> SourceNamedShelf<Int>.Ledger<String> {
    return SourceNamedShelf<Int>.Ledger(tag: tag, element: element)
}

public func describeShelfLedgerIntString(_ value: SourceNamedShelf<Int>.Ledger<String>) -> String {
    return value.summary
}
