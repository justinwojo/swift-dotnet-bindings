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

// MARK: - Optional CoreGraphics Types

/// Optional CGPoint parameter — tests value-type optional for Apple framework frozen struct.
public func processOptionalPoint(_ point: CGPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

/// Optional CGRect parameter — tests value-type optional for larger Apple framework struct.
public func processOptionalRect(_ rect: CGRect?) -> String {
    guard let r = rect else { return "nil" }
    return "\(r.origin.x),\(r.origin.y) \(r.size.width)x\(r.size.height)"
}

/// CGFloat scaling — tests Double mapping for CGFloat parameters and return.
public func scaleCGFloat(_ value: CGFloat, by factor: CGFloat) -> CGFloat {
    return value * factor
}

// MARK: - CoreGraphics CFType Reference Types
//
// Regression coverage for CGImage/CGColor projected as IntPtr instead of managed wrappers:
//
// CGImage / CGColor (and the CG* CFType family) must project to the canonical
// `CoreGraphics.CGImage` / `CoreGraphics.CGColor` wrappers from dotnet/macios,
// not to `System.IntPtr`. Pre-fix the typedb registered these under
// `managedTypeName="IntPtr"`, which gave consumers a raw pointer with no managed
// wrapper, no compile-time type safety, and no automatic CFRetain/CFRelease
// management. The fix routes them through ObjCBridgedProjection which calls
// `Runtime.GetINativeObject<CoreGraphics.CGImage>(ptr, owns: false)` (and the
// CGColor equivalent), preserving CFType ownership semantics.

/// Returns a non-null `CGColor` (sRGB red). The fact that the generated C#
/// signature is `CoreGraphics.CGColor MakeRedColor()` rather than
/// `System.IntPtr MakeRedColor()` is the regression assertion at compile time.
public func makeRedColor() -> CGColor {
    return CGColor(srgbRed: 1.0, green: 0.0, blue: 0.0, alpha: 1.0)
}

/// Returns an optional `CGColor`. Mirrors the MusicKit `Artwork.BackgroundColor`
/// shape — the consumer-facing type must be `CoreGraphics.CGColor?`, not
/// `System.IntPtr?`.
public func maybeColor(_ withColor: Bool) -> CGColor? {
    return withColor ? CGColor(srgbRed: 0.0, green: 1.0, blue: 0.0, alpha: 1.0) : nil
}

/// Returns a 1x1 `CGImage` drawn into an in-memory bitmap context. The consumer-facing
/// return type must be `CoreGraphics.CGImage?`, not `System.IntPtr?`.
public func makeOnePixelImage() -> CGImage? {
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let context = CGContext(
        data: nil,
        width: 1,
        height: 1,
        bitsPerComponent: 8,
        bytesPerRow: 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else {
        return nil
    }
    context.setFillColor(red: 0.5, green: 0.5, blue: 0.5, alpha: 1.0)
    context.fill(CGRect(x: 0, y: 0, width: 1, height: 1))
    return context.makeImage()
}
