// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A protocol whose only requirement is a stored-property-like `storage` member, plus a generic
// subscript supplied by a protocol extension (the "typed property bag" idiom). The subscript is
// NOT a requirement: every conformer inherits it from the extension. Treating it as one makes the
// conformance look like it needs a subscript-level generic witness it can never provide, and the
// protocol is dropped even though its real requirement dispatches normally.

import Foundation

public struct ModeledProperty<Value> {
    public let key: String
    public let defaultValue: Value
    public init(key: String, defaultValue: Value) {
        self.key = key
        self.defaultValue = defaultValue
    }
}

public protocol ExtensionSubscriptModeled {
    var storage: [String: Int32] { get set }
}

extension ExtensionSubscriptModeled {
    public subscript<Property>(property: ModeledProperty<Property>) -> Property {
        get { (storage[property.key] as? Property) ?? property.defaultValue }
        set {
            if let value = newValue as? Int32 {
                storage[property.key] = value
            }
        }
    }
}

/// Reads through the extension subscript on the existential, which reaches the `storage`
/// requirement.
public func extensionSubscriptModeledRead(_ model: any ExtensionSubscriptModeled, key: String) -> Int32 {
    model[ModeledProperty(key: key, defaultValue: Int32(-1))]
}
