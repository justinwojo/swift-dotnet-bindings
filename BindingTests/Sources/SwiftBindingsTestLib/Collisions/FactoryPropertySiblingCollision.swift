// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Constructor-factory recovery next to a PROPERTY that already owns the factory's name.
//
// Two labeled initializers over the same projected parameter type recover as label-named static
// factories (`CreateWith{Label}`). A C# class cannot hold a property and a method of the same
// name (CS0102), so a factory name has to clear the type's property and nested-type names as well
// as its methods' projected signatures. Here `createWithFoo` is a property, so the label rung is
// unavailable to `init(foo:)`; the type-derived rung cannot tell the two initializers apart
// either, so neither recovers and the positional initializer keeps the plain constructor. What
// must NOT happen is a `CreateWithFoo` factory emitted against the property.
//
// There is deliberately no `createWithFoo(_:)` method: a sibling method with that projected
// signature would block the label rung by accident and hide the property collision.

public final class FactoryPropertySiblingHost {
    public let source: String
    public let value: Int32

    /// Owns the C# name `CreateWithFoo`.
    public var createWithFoo: Int32 { value }

    public init(_ value: Int32) {
        self.source = "positional"
        self.value = value
    }

    public init(foo value: Int32) {
        self.source = "foo"
        self.value = value
    }

    public init(bar value: Int32) {
        self.source = "bar"
        self.value = value
    }
}
