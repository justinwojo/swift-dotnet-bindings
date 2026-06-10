// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol for Optional Existential Property Tests

/// Protocol for testing optional existential property accessors.
/// Tests classes with optional protocol-typed stored properties.
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

// MARK: - EC1 Factory Functions

/// Creates a RenderableHolder with a concrete SimpleRenderable value.
public func makeRenderableHolder(name: String) -> RenderableHolder {
    return RenderableHolder(primary: SimpleRenderable(name: name))
}

/// Creates a RenderableHolder with nil primary.
public func makeEmptyRenderableHolder() -> RenderableHolder {
    return RenderableHolder()
}

// MARK: - Multi-Protocol Composition for EC2 Tests

/// Protocol for labeling — combined with Renderable to form EC2 (2-witness-table) existential.
public protocol Labelable {
    func label() -> String
}

/// Class with an optional EC2 existential property `(any Renderable & Labelable)?`.
/// Tests optional existential getter/setter paths with ExistentialContainer2 (48 bytes).
public class LabelableRenderableHolder {
    public var item: (any Renderable & Labelable)?

    public init() { self.item = nil }
    public init(item: any Renderable & Labelable) { self.item = item }

    public func getItemDescription() -> String {
        guard let item = item else { return "none" }
        return "\(item.render())+\(item.label())"
    }
}

/// Concrete struct conforming to both Renderable and Labelable for EC2 boxing.
public struct LabelableRenderable: Renderable, Labelable {
    public let name: String
    public init(name: String) { self.name = name }
    public func render() -> String { return "Render(\(name))" }
    public func label() -> String { return "Label(\(name))" }
}

// MARK: - EC2 Factory Functions

/// Creates a LabelableRenderableHolder with a concrete LabelableRenderable value.
public func makeLabelableRenderableHolder(name: String) -> LabelableRenderableHolder {
    return LabelableRenderableHolder(item: LabelableRenderable(name: name))
}

/// Creates a LabelableRenderableHolder with nil item.
public func makeEmptyLabelableRenderableHolder() -> LabelableRenderableHolder {
    return LabelableRenderableHolder()
}
