// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Basic Protocols

/// Protocol with a read-only property and a method.
public protocol Describable {
    var description: String { get }
    func describe() -> String
}

/// Protocol with an identifier property.
public protocol TestIdentifiable {
    var id: String { get }
}

/// Protocol inheriting from Describable.
public protocol Displayable: Describable {
    func display() -> String
}

/// Protocol with a get+set property and methods.
public protocol HasValue {
    var value: Int32 { get set }
    func getValue() -> Int32
    mutating func setValue(_ newValue: Int32)
}
