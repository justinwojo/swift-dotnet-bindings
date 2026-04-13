// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Type Aliases

/// Type alias for DependencyTokenA — tests cross-module alias resolution.
/// Analogous to FamilyControls.ApplicationToken → ManagedSettings.Token<Application>.
public typealias TokenA = DependencyTokenA

/// Type alias for DependencyTokenB.
public typealias TokenB = DependencyTokenB

/// Holder that uses cross-module type aliases in its API.
/// Tests that the generator correctly resolves aliases to their canonical types
/// from the dependency module's type database.
public struct TokenHolder {
    public let tokenA: DependencyTokenA
    public let tokenB: DependencyTokenB

    public init(idA: Int32, idB: Int32) {
        self.tokenA = DependencyTokenA(identifier: idA)
        self.tokenB = DependencyTokenB(identifier: idB)
    }

    public var tokenADescription: String {
        return tokenA.describe()
    }

    public var tokenBDescription: String {
        return tokenB.describe()
    }
}

/// Free function that takes a cross-module aliased type.
public func describeTokenA(_ token: DependencyTokenA) -> String {
    return token.describe()
}

/// Free function that returns a cross-module aliased type.
public func makeTokenA(id: Int32) -> DependencyTokenA {
    return DependencyTokenA(identifier: id)
}
