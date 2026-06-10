// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol for Existential Return Tests

/// Protocol with a property and method — returned as `any ERTestProtocol` from
/// factory APIs. Tests the pattern where methods/properties return protocol existentials.
public protocol ERTestProtocol {
    var label: String { get }
    func describe() -> String
}

// MARK: - Concrete Conforming Type

/// Concrete class conforming to ERTestProtocol.
public class ERTestConcreteItem: ERTestProtocol {
    public var label: String
    public init(label: String) { self.label = label }
    public func describe() -> String { "Item: \(label)" }
}

// MARK: - Factory Returning Existentials

/// Factory class with property/method/static returning `any ERTestProtocol`.
/// This is the pattern that triggers the R3 regression.
public class ERTestFactory {
    public init() {}

    /// Property returning existential.
    public var defaultItem: any ERTestProtocol {
        ERTestConcreteItem(label: "default")
    }

    /// Method returning existential.
    public func createItem(label: String) -> any ERTestProtocol {
        ERTestConcreteItem(label: label)
    }

    /// Static method returning existential.
    public static func shared() -> any ERTestProtocol {
        ERTestConcreteItem(label: "shared")
    }
}

// MARK: - Constructor Taking Existential

/// Class whose constructor takes `any ERTestProtocol` — tests existential parameter round-trip.
public class ERTestHolder {
    private let item: any ERTestProtocol
    public init(item: any ERTestProtocol) { self.item = item }
    public var heldLabel: String { item.label }
}

// MARK: - Factory with Closure + Existential Return

/// Static factory method taking a closure and returning `any ERTestProtocol`.
public class ERTestFilterFactory {
    public init() {}
    public static func custom(filter: @escaping (String) -> Bool) -> any ERTestProtocol {
        ERTestConcreteItem(label: "custom")
    }
}
