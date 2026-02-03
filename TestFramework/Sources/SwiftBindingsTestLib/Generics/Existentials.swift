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
