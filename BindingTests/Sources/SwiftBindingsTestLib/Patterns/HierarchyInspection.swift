// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreGraphics

// MARK: - Hierarchy Inspection Pattern (Lottie animation hierarchy)
// Tests the pattern where a container type provides:
// - allKeypaths() returning [String]
// - point/rect coordinate conversion with optional returns
// - setNodeEnabled(isEnabled:keypath:) for toggling nodes
// - getValue(for:atFrame:) for frame-based queries
// This models Lottie's LottieAnimationView hierarchy inspection API.

/// Represents a node in an animation layer tree.
public struct LayerNode {
    public var name: String
    public var isEnabled: Bool
    public var x: CGFloat
    public var y: CGFloat
    public var width: CGFloat
    public var height: CGFloat

    public init(name: String, isEnabled: Bool, x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat) {
        self.name = name
        self.isEnabled = isEnabled
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

/// Container with hierarchy inspection methods — models LottieAnimationView.
public final class LayerContainer {
    private var nodes: [String: LayerNode] = [:]
    private var keypathOrder: [String] = []

    public init() {}

    /// Add a layer node at the given keypath.
    public func addNode(_ node: LayerNode, at keypath: String) {
        nodes[keypath] = node
        keypathOrder.append(keypath)
    }

    /// Return all keypaths in the hierarchy.
    /// Models: LottieAnimationView.allHierarchyKeypaths
    public func allKeypaths() -> [String] {
        return keypathOrder
    }

    /// Number of nodes in the hierarchy.
    public func nodeCount() -> Int32 {
        return Int32(nodes.count)
    }

    /// Convert a point from container coordinates to a layer's local coordinates.
    /// Returns nil if the keypath doesn't exist. Assumes origin-offset only (no scale/rotation).
    /// Models: LottieAnimationView.convert(point:toLayerAt:)
    public func convertPoint(x: CGFloat, y: CGFloat, toLayerAt keypath: String) -> CGPoint? {
        guard let node = nodes[keypath] else { return nil }
        return CGPoint(x: x - node.x, y: y - node.y)
    }

    /// Convert a rect from container coordinates to a layer's local coordinates.
    /// Returns nil if the keypath doesn't exist.
    /// Models: LottieAnimationView.convert(rect:toLayerAt:)
    public func convertRect(x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat, toLayerAt keypath: String) -> CGRect? {
        guard let node = nodes[keypath] else { return nil }
        return CGRect(x: x - node.x, y: y - node.y, width: width, height: height)
    }

    /// Enable or disable a node at the given keypath.
    /// Models: LottieAnimationView.setNodeIsEnabled(isEnabled:keypath:)
    public func setNodeEnabled(isEnabled: Bool, keypath: String) {
        nodes[keypath]?.isEnabled = isEnabled
    }

    /// Check if a node is enabled at the given keypath.
    public func isNodeEnabled(keypath: String) -> Bool {
        return nodes[keypath]?.isEnabled ?? false
    }

    /// Get a node's value (width * height) at a simulated frame.
    /// Models: LottieAnimationView.getValue(for:atFrame:)
    public func getValueAtFrame(keypath: String, frame: Double) -> Double? {
        guard let node = nodes[keypath] else { return nil }
        // Simulate frame-based interpolation: scale by frame/60
        let scale = frame / 60.0
        return Double(node.width * node.height) * scale
    }

    /// Log all keypaths — returns a concatenated string for testing.
    /// Models: LottieAnimationView.logHierarchyKeypaths()
    public func logKeypaths() -> String {
        return keypathOrder.joined(separator: "\n")
    }
}
