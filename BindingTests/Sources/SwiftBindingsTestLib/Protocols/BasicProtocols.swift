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

// MARK: - N1: Protocol with Default Implementation (Lottie AnimationImageProvider pattern)

/// Protocol where one method has a default implementation via extension.
/// Generator should emit `throw new NotSupportedException(...)` for the default.
public protocol Configurable {
    var configName: String { get }
    func configure() -> String
}

extension Configurable {
    public func configure() -> String {
        return "Default: \(configName)"
    }
}

// MARK: - N4: Marker Protocol (zero members, SVGView IXMLNode pattern)

/// Empty protocol used as a type constraint.
public protocol Taggable {}

// MARK: - AB2: 3-Level Protocol Inheritance Chain (SnapKit ConstraintDSL pattern)

/// Base protocol in a 3-level chain.
public protocol BaseRule {
    var ruleName: String { get }
}

/// Mid-level protocol inheriting from BaseRule.
public protocol InputValidation: BaseRule {
    func validate(input: String) -> Bool
}

/// Leaf protocol inheriting from InputValidation.
public protocol StrictInputValidation: InputValidation {
    var strictLevel: Int32 { get }
}

// MARK: - Static Protocol Members (static abstract emission)

/// Protocol with static members for testing static abstract emission.
public protocol DefaultInitializableValue {
    static var defaultValue: Int32 { get }
    static func create(withValue value: Int32) -> Int32
    func getValue() -> Int32  // instance method for mixed testing
}

/// Concrete type implementing DefaultInitializableValue.
public struct SimpleDefault: DefaultInitializableValue {
    public static var defaultValue: Int32 { return 42 }
    public static func create(withValue value: Int32) -> Int32 { return value }
    public func getValue() -> Int32 { return Self.defaultValue }
}
