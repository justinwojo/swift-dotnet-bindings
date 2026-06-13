// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential Types

/// Accepts any Describable (existential container).
public func acceptsAnyDescribable(_ item: any Describable) -> String {
    return item.describe()
}

/// Returns an existential type.
public func makeDescribable(text: String) -> any Describable {
    return SimpleItem(id: "generated", label: text)
}

/// Accepts a composition existential.
public func acceptsComposition(_ item: any Describable & TestIdentifiable) -> String {
    return "[\(item.id)] \(item.describe())"
}

/// Returns a composition existential.
public func makeIdentifiableDescribable(id: String, text: String) -> any Describable & TestIdentifiable {
    return SimpleItem(id: id, label: text)
}

/// Accepts an existential array.
public func describeAll(_ items: [any Describable]) -> [String] {
    return items.map { $0.describe() }
}

// MARK: - Opaque Return Types (`some Protocol`)
// Tests: Functions and properties returning `some Protocol` (opaque return type)
// Expected C#: Concrete type emission (the compiler knows the real type)
// Limitation: Opaque return types are not yet supported by the generator

/// Returns an opaque Describable type.
public func makeOpaqueDescribable(text: String) -> some Describable {
    return SimpleItem(id: "opaque", label: text)
}

/// Struct with a computed property returning an opaque type.
public struct OpaqueProvider {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Computed property returning `some Describable`.
    public var opaqueItem: some Describable {
        return SimpleItem(id: "provider", label: label)
    }
}

/// Returns an opaque Describable & TestIdentifiable composition.
public func makeOpaqueComposition(id: String, text: String) -> some Describable & TestIdentifiable {
    return SimpleItem(id: id, label: text)
}

// MARK: - R1: ExistentialContainer0 (Any)

/// Class with a property returning unconstrained `Any`.
/// Tests ExistentialContainer0 marshalling (distinct from ExistentialContainer1).
public class AnyHolder {
    private let stored: Any

    public init(intValue: Int32) {
        self.stored = intValue
    }

    public init(stringValue: String) {
        self.stored = stringValue
    }

    public var base: Any {
        return stored
    }
}

// MARK: - Existential Union (PAT protocol with known conformers)

/// Protocol with associated type — cannot become a simple C# interface.
/// The generator should emit ExistentialUnion with try-cast to known conformers.
public protocol AttributeKind {
    associatedtype Value
    var label: String { get }
    var value: Value { get }
}

/// Conformer 1: color attribute with String value.
@frozen public struct ColorAttribute: AttributeKind {
    public typealias Value = String
    public let label: String
    public let value: String

    public init(label: String, value: String) {
        self.label = label
        self.value = value
    }
}

/// Conformer 2: size attribute with Int32 value.
@frozen public struct SizeAttribute: AttributeKind {
    public typealias Value = Int32
    public let label: String
    public let value: Int32

    public init(label: String, value: Int32) {
        self.label = label
        self.value = value
    }
}

/// Conformer 3: flag attribute with Bool value.
@frozen public struct FlagAttribute: AttributeKind {
    public typealias Value = Bool
    public let label: String
    public let value: Bool

    public init(label: String, value: Bool) {
        self.label = label
        self.value = value
    }
}

/// Container that holds an existential of the PAT protocol.
/// The `attribute` property returns `any AttributeKind` — the generator should
/// emit this as ExistentialUnion since the protocol has associated types.
public struct AttributeHolder {
    private let stored: any AttributeKind

    public init(color: String) {
        self.stored = ColorAttribute(label: "color", value: color)
    }

    public init(size: Int32) {
        self.stored = SizeAttribute(label: "size", value: size)
    }

    public init(flag: Bool) {
        self.stored = FlagAttribute(label: "flag", value: flag)
    }

    /// Constructor taking a PAT existential directly. The generator cannot project
    /// `any AttributeKind` (associated type), so this parameter degrades to `object`.
    /// Exercises the constructor existential-degradation flag + SWIFTBIND023 recording
    /// path — constructors previously emitted no `[UnsupportedSwiftType]` marker at all.
    public init(existing attribute: any AttributeKind) {
        self.stored = attribute
    }

    public var attribute: any AttributeKind {
        return stored
    }

    public var attributeLabel: String {
        return stored.label
    }
}

/// Free function returning existential of PAT protocol.
public func makeColorAttribute(name: String, color: String) -> any AttributeKind {
    return ColorAttribute(label: name, value: color)
}

/// Free function returning existential of PAT protocol.
public func makeSizeAttribute(name: String, size: Int32) -> any AttributeKind {
    return SizeAttribute(label: name, value: size)
}
