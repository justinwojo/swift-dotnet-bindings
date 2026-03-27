// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol for Optional Existential Property Tests

/// Protocol for testing optional existential property accessors.
/// Pattern from 11+ validation libraries (Nuke, Kingfisher, etc.):
/// classes with optional protocol-typed stored properties.
public protocol Renderable {
    func render() -> String
}

// MARK: - Class with Optional Existential Property

/// Class with an optional existential property `(any Renderable)?`.
/// Tests optional existential getter/setter paths in ExistentialHandler.
public class RenderableHolder {
    public var primary: (any Renderable)?

    public init() {
        self.primary = nil
    }

    public init(primary: any Renderable) {
        self.primary = primary
    }

    public func getPrimaryDescription() -> String {
        return primary?.render() ?? "none"
    }
}

// MARK: - Concrete Conformer

/// Concrete struct conforming to Renderable for testing existential boxing.
public struct SimpleRenderable: Renderable {
    public let name: String
    public init(name: String) { self.name = name }
    public func render() -> String { return "SimpleRenderable(\(name))" }
}

// MARK: - Factory Functions

/// Creates a RenderableHolder with a concrete SimpleRenderable value.
public func makeRenderableHolder(name: String) -> RenderableHolder {
    return RenderableHolder(primary: SimpleRenderable(name: name))
}

/// Creates a RenderableHolder with nil primary.
public func makeEmptyRenderableHolder() -> RenderableHolder {
    return RenderableHolder()
}
