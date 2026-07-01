// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @objc class-bound protocol existential (and its Optional)
//
// Regression repro for an @objc protocol whose existential is ALSO class-bound
// (carries an AnyObject / NSObjectProtocol requirement). Such a protocol's
// existential `any P` — and `(any P)?` — is a single 8-byte Objective-C object
// pointer with NO Swift witness-table word (dispatch goes through the ObjC selector
// table), identical to AnyObject. Critically, an @objc protocol exports NO Swift
// `…Mp` protocol descriptor.
//
// The generator used to route any class-bound arity-1 existential through the
// 16-byte `ClassExistentialContainer1` carrier and emit a module-init
// `RegisterClassBoundExistentialMetadata(lib, "$s…Mp")`. For an @objc protocol the
// descriptor load silently fails, leaving the carrier's metadata unregistered, so
// the first `SwiftOptional<ClassExistentialContainer1>` static init throws
// "Unable to get type metadata for type ClassExistentialContainer1" — even when the
// passed value is nil. This mirrors a real-world shape: a constructor taking an optional
// class-bound `@objc` protocol existential, where the `@objc` protocol is
// `NSObjectProtocol`-rooted and composed with a second protocol.
//
// The fix tracks @objc-ness on the protocol record and diverts @objc existentials
// off the ClassExistentialContainer1 carrier onto the descriptor-free opaque
// container path. This fixture exercises the constructor (the exact blocking shape),
// a property getter, and a method — all in `(any P)?` Optional position, with nil
// and with a Swift-vended conformer.

/// @objc protocol that is also class-bound (`: AnyObject`). Its existential carries
/// the ObjC representation (single object pointer, no Swift witness table) and the
/// protocol exports no Swift `…Mp` descriptor.
@objc public protocol ObjCClassBoundShape: AnyObject {
    @objc var tag: Int32 { get }
}

/// Concrete @objc conformer. `any ObjCClassBoundShape` values handed to C# wrap this.
@objc public class ObjCShapeThing: NSObject, ObjCClassBoundShape {
    public let tag: Int32
    @objc public init(tag: Int32) {
        self.tag = tag
    }
}

/// Vends a Swift-side conformer as the class-bound @objc existential.
public func makeObjCShape(_ tag: Int32) -> any ObjCClassBoundShape {
    return ObjCShapeThing(tag: tag)
}

/// Box whose constructor takes the Optional class-bound @objc existential — the exact
/// `init?(content: (any P)?)` shape. Constructing it with `nil` used to throw at the
/// `SwiftOptional<ClassExistentialContainer1>` static initializer.
public class ObjCShapeBox {
    private let content: (any ObjCClassBoundShape)?

    /// Settable Optional class-bound @objc existential — the exact `var content: (any P)?`
    /// shape. Exercises the setter path, which
    /// must marshal a single by-value ObjC object pointer (nil → null), not the 16-byte
    /// `ClassExistentialContainer1` decomposed (container + hasValue) carrier.
    public var mutableStored: (any ObjCClassBoundShape)?

    public init(_ content: (any ObjCClassBoundShape)?) {
        self.content = content
    }

    /// Property getter returning the Optional class-bound @objc existential.
    public var stored: (any ObjCClassBoundShape)? {
        return content
    }

    /// Reads the conformer's witness through the existential (-1 when empty).
    public func storedTag() -> Int32 {
        return content?.tag ?? -1
    }

    /// Reads the conformer's witness through the settable existential (-1 when empty).
    public func mutableStoredTag() -> Int32 {
        return mutableStored?.tag ?? -1
    }
}

/// Round-trips the Optional class-bound @objc existential through a free function
/// (method param + return position).
public func echoObjCShape(_ shape: (any ObjCClassBoundShape)?) -> (any ObjCClassBoundShape)? {
    return shape
}

// MARK: - Reverse dispatch of a *plain* C# conformer through the @objc existential
//
// The two functions below take the class-bound @objc existential as a PARAMETER and
// dispatch `.tag` on it — reading the conformer's witness back out. When the caller
// hands in a plain managed conformer (a C# class implementing the generated interface,
// with no Swift-vended backing object), the generator auto-wraps it into an EveryProtocol
// proxy and passes the proxy's single bare ObjC object pointer on the wire. Swift then
// reconstructs `any ObjCClassBoundShape` from that pointer and dispatches `.tag` straight
// back into the C# implementation through the vtable. `readObjCShapeTag` exercises the
// NON-optional projection path; `readOptionalObjCShapeTag` exercises the Optional path
// (nil → -1). These are the reverse-dispatch gate for a plain conformer flowing *into*
// an @objc existential parameter.

/// Dispatches `.tag` on a non-optional class-bound @objc existential parameter.
public func readObjCShapeTag(_ shape: any ObjCClassBoundShape) -> Int32 {
    return shape.tag
}

/// Dispatches `.tag` on an Optional class-bound @objc existential parameter (-1 when nil).
public func readOptionalObjCShapeTag(_ shape: (any ObjCClassBoundShape)?) -> Int32 {
    return shape?.tag ?? -1
}
