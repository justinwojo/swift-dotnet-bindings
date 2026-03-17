// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreGraphics

// MARK: - W1: CoreGraphics Types (CGPoint/CGSize/CGRect/CGFloat)
// Runtime types exist (Swift.CGPoint/CGSize/CGRect) but have zero test coverage.
// Every UI-oriented library uses these extensively.

public struct LayoutConfig {
    public var origin: CGPoint
    public var size: CGSize
    public var frame: CGRect
    public var spacing: CGFloat

    public init(origin: CGPoint, size: CGSize) {
        self.origin = origin
        self.size = size
        self.frame = CGRect(origin: origin, size: size)
        self.spacing = 0
    }

    public func describe() -> String {
        return "(\(origin.x),\(origin.y)) \(size.width)x\(size.height)"
    }
}

// MARK: - CoreGraphics Free Functions

public func createPoint(x: CGFloat, y: CGFloat) -> CGPoint {
    return CGPoint(x: x, y: y)
}

public func createSize(width: CGFloat, height: CGFloat) -> CGSize {
    return CGSize(width: width, height: height)
}

public func createRect(x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat) -> CGRect {
    return CGRect(x: x, y: y, width: width, height: height)
}

public func describeRect(_ rect: CGRect) -> String {
    return "(\(rect.origin.x),\(rect.origin.y) \(rect.size.width)x\(rect.size.height))"
}

public func rectArea(_ rect: CGRect) -> CGFloat {
    return rect.size.width * rect.size.height
}
