// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Type References

/// Uses DependencyPoint from the dependency module as parameter and return type.
/// Tests that the generator correctly resolves cross-module type references.
public func transformDependencyPoint(_ point: DependencyPoint, scale: Double) -> DependencyPoint {
    return DependencyPoint(x: point.x * scale, y: point.y * scale)
}

/// Uses DependencyConfig from the dependency module.
public func upgradeDependencyConfig(_ config: DependencyConfig) -> DependencyConfig {
    return DependencyConfig(name: config.name, version: config.version + 1)
}

/// Uses DependencyService class from the dependency module.
public func toggleDependencyService(_ service: DependencyService) -> String {
    service.isActive = !service.isActive
    return service.status()
}

// MARK: - Cross-Module Protocol Conformance

/// A local struct that conforms to DependencyProtocol from the dependency module.
/// Tests that cross-module protocol conformances are correctly emitted.
public struct LocalConformant: DependencyProtocol {
    public var identifier: String
    public var tag: Int32

    public init(identifier: String, tag: Int32 = 0) {
        self.identifier = identifier
        self.tag = tag
    }

    public func describe() -> String {
        return "Local[\(tag)]: \(identifier)"
    }
}

/// Factory for creating LocalConformant instances.
public func makeLocalConformant(identifier: String, tag: Int32) -> LocalConformant {
    return LocalConformant(identifier: identifier, tag: tag)
}

/// Accepts any DependencyProtocol and returns its description.
/// Tests that the generated binding can pass local conformants to dependency protocol functions.
public func describeLocalConformant(_ conformant: some DependencyProtocol) -> String {
    return conformant.describe()
}

// NOTE: Module name = type name collision (Reachability pattern) is tested
// through validation libraries. Swift issue #56573 prevents including this
// pattern in a library-evolution-enabled module used as a build dependency.
