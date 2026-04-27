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

// MARK: - Cross-Module Class Inheritance (Bug #14)

/// Open base class living in the dependency module. The main module defines a subclass —
/// the parser must resolve `DependencyBaseEntity` via the global TypeDatabase rather than
/// the local `_typeDecls` dictionary, otherwise the C# emitter flattens the hierarchy.
open class DependencyBaseEntity {
    public var label: String
    public init(label: String) {
        self.label = label
    }

    open func describe() -> String {
        return "Base[\(label)]"
    }

    open func tag() -> Int32 {
        return 0
    }
}

/// Mid-tier class — also in the dependency module — for testing 3-level cross-module chains.
/// A subclass in the main module derived from this exercises the SuperclassTypeName walk.
open class DependencyMidEntity: DependencyBaseEntity {
    public var midTag: Int32

    public init(label: String, midTag: Int32) {
        self.midTag = midTag
        super.init(label: label)
    }

    open override func describe() -> String {
        return "Mid[\(label):\(midTag)]"
    }

    open override func tag() -> Int32 {
        return midTag
    }
}

/// Polymorphic accept: takes a base reference, returns its describe() result.
/// The C# call site must accept any subclass without an explicit cast — that's the
/// usability symptom Bug #14 fixes.
public func describeBaseEntity(_ entity: DependencyBaseEntity) -> String {
    return entity.describe()
}

/// Polymorphic accept: returns the runtime tag through the base.
public func readBaseEntityTag(_ entity: DependencyBaseEntity) -> Int32 {
    return entity.tag()
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
