// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cross-Module Types

/// Protocol for cross-module conformance testing.
/// Main test library types conform to this protocol to test
/// cross-module protocol conformance in generated bindings.
public protocol DependencyProtocol {
    var identifier: String { get }
    func describe() -> String
}

/// Frozen struct from the dependency module.
/// Used as parameter/return type in main test library functions
/// to test cross-module type references.
@frozen
public struct DependencyPoint {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    public func distanceFromOrigin() -> Double {
        return (x * x + y * y).squareRoot()
    }

    public func translated(dx: Double, dy: Double) -> DependencyPoint {
        return DependencyPoint(x: x + dx, y: y + dy)
    }
}

/// Non-frozen struct from the dependency module.
/// Tests cross-module opaque type handling.
public struct DependencyConfig {
    public var name: String
    public var version: Int32

    public init(name: String, version: Int32) {
        self.name = name
        self.version = version
    }

    public func summary() -> String {
        return "\(name) v\(version)"
    }
}

/// Class from the dependency module.
/// Tests cross-module class reference handling.
public class DependencyService {
    public var name: String
    public var isActive: Bool

    public init(name: String, isActive: Bool = true) {
        self.name = name
        self.isActive = isActive
    }

    public func status() -> String {
        return isActive ? "\(name): active" : "\(name): inactive"
    }
}

/// Enum from the dependency module.
/// Used for cross-module enum parameter/return testing.
@frozen
public enum DependencyStatus: Int32 {
    case unknown = 0
    case pending = 1
    case active = 2
    case inactive = 3

    public var label: String {
        switch self {
        case .unknown: return "Unknown"
        case .pending: return "Pending"
        case .active: return "Active"
        case .inactive: return "Inactive"
        }
    }
}

// MARK: - Free Functions

/// Creates a DependencyPoint.
public func makeDependencyPoint(x: Double, y: Double) -> DependencyPoint {
    return DependencyPoint(x: x, y: y)
}

/// Creates a DependencyConfig.
public func makeDependencyConfig(name: String, version: Int32) -> DependencyConfig {
    return DependencyConfig(name: name, version: version)
}

/// Creates a DependencyService.
public func makeDependencyService(name: String) -> DependencyService {
    return DependencyService(name: name)
}

/// Accepts a DependencyProtocol conformant and returns its description.
public func describeDependency(_ dep: some DependencyProtocol) -> String {
    return dep.describe()
}

// MARK: - Cross-Module Type Alias Support

/// Concrete token type A — analogous to a specific Token instantiation.
/// Used to test cross-module type alias resolution.
@frozen
public struct DependencyTokenA {
    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
    }

    public func describe() -> String {
        return "Token(\(identifier))"
    }
}

/// Concrete token type B — analogous to a different Token instantiation.
/// Used to test cross-module type alias resolution.
@frozen
public struct DependencyTokenB {
    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
    }

    public func describe() -> String {
        return "Token(\(identifier))"
    }
}
