// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreGraphics

// MARK: - Protocol Requirement Taking an ObjC-Bridged Reference Type
//
// `CGContext` is an ObjC-BRIDGED reference type: it wraps a CFTypeRef, its C# projection is an
// existing platform binding class, and that class exposes the native pointer through a bare
// `.Handle` — it has no ISwiftObject `.Payload` SafeHandle. Critically it is NOT NSObject-rooted,
// so an "is this class ObjC-rooted?" test alone answers FALSE for it. A marshalling site that
// decides between `.Handle` and `.Payload` on the ObjC-rooted half of the question therefore
// emits `.Payload.DangerousGetHandle()` against a type with no such member, and the binding
// fails to compile.
//
// The proxy witness-dispatch forwarding path is the one that must get this right here: declaring
// the requirement on a protocol and passing the protocol as an existential forces the generator
// to emit a proxy whose implementation forwards the parameter's native handle back into Swift.
// Every other CoreGraphics fixture in the corpus uses VALUE types (CGRect/CGPoint/CGSize) or keeps
// the reference type as a local, so this reference-type-through-a-witness shape was uncovered.

/// Protocol whose requirement takes an ObjC-bridged CoreGraphics reference type.
public protocol CanvasRendering {
    /// Requirement carrying the bridged reference type in parameter position.
    func render(into context: CGContext, width: Int32)

    /// Companion requirement with no bridged parameter, so the proxy has a plain slot too.
    var rendererName: String { get }
}

/// Swift conformer, so the forward (Swift-side) direction is exercised as well as the proxy.
public final class SolidFillRenderer: CanvasRendering {
    public let fillWidth: Int32

    public init(fillWidth: Int32) {
        self.fillWidth = fillWidth
    }

    public var rendererName: String { "SolidFill(\(fillWidth))" }

    public func render(into context: CGContext, width: Int32) {
        context.setFillColor(gray: 1.0, alpha: 1.0)
        context.fill(CGRect(x: 0, y: 0, width: CGFloat(width), height: 1))
    }
}

/// Existential parameter — forces the proxy class and its witness-dispatch forwarding to emit.
public func nameOfRenderer(_ renderer: any CanvasRendering) -> String {
    return renderer.rendererName
}

/// Existential RETURN of a Swift conformer. C# receives the protocol as a proxy over a Swift
/// value, so calling the bridged-reference requirement on it runs the proxy's FORWARD body — the
/// one that has to hand Swift the bridged type's native pointer. Without a Swift-valued
/// existential to hold, that body compiles but nothing can reach it, and whether the pointer it
/// forwards is actually usable stays unproven.
public func makeSolidFillRenderer(_ fillWidth: Int32) -> any CanvasRendering {
    return SolidFillRenderer(fillWidth: fillWidth)
}

/// Drives the bridged-reference requirement through the existential, so the forwarding body is
/// reachable rather than merely emitted.
public func renderOnePixel(_ renderer: any CanvasRendering) -> Bool {
    guard let context = CGContext(
        data: nil,
        width: 1,
        height: 1,
        bitsPerComponent: 8,
        bytesPerRow: 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else {
        return false
    }
    renderer.render(into: context, width: 1)
    return true
}
