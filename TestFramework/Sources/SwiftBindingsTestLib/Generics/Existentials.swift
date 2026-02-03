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
