// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Label-only-overload disambiguation (DuplicateSignature)
//
// A Cocoa-style delegate protocol commonly declares several requirements that share
// a base name and identical parameter TYPES, differing ONLY by their argument labels:
//
//   func conversationManager(_ manager: Int32, didActivate session: Int32) -> Int32
//   func conversationManager(_ manager: Int32, didDeactivate session: Int32) -> Int32
//
// Both project to the same C# overload `ConversationManager(int, int)` once the labels
// are erased, so the second was previously dropped as a DuplicateSignature. They must
// now survive as DISTINCT C# members named ObjC-selector style from the labels —
// `ConversationManagerDidActivate` / `ConversationManagerDidDeactivate` — each routed
// to its own reverse-dispatch vtable slot. These class-bound protocols are implemented
// in C# and called back into from Swift, so a slot mix-up (or a collapsed member) shows
// up as a wrong tag/return value at runtime, not just a compile error.

/// LCK-shape delegate: a base name with a `_`-labeled sender and one labeled scalar,
/// colliding on the label-erased projection.
public protocol ConversationManagerDelegate: AnyObject {
    func conversationManager(_ manager: Int32, didActivate session: Int32) -> Int32
    func conversationManager(_ manager: Int32, didDeactivate session: Int32) -> Int32
}

/// Drives each requirement from Swift into the C# conformance and returns whatever the
/// delegate returned, so the C# test can prove the call routed to the correct member
/// (the return values differ per method) AND that the right slot fired (recorded in the
/// impl). A collapsed/mis-routed member would surface as the wrong return value here.
public class ConversationManagerHarness {
    public init() {}

    public func activate(_ delegate: ConversationManagerDelegate, manager: Int32, session: Int32) -> Int32 {
        return delegate.conversationManager(manager, didActivate: session)
    }

    public func deactivate(_ delegate: ConversationManagerDelegate, manager: Int32, session: Int32) -> Int32 {
        return delegate.conversationManager(manager, didDeactivate: session)
    }
}

/// RoomPlan-triple shape: three label-only overloads of `captureSession` that all erase
/// to `CaptureSession(int, int)`. Exercises the three-way collision (more than a pair),
/// disambiguating to CaptureSessionDidAdd / CaptureSessionDidChange / CaptureSessionDidUpdate.
public protocol CaptureSessionObserver: AnyObject {
    func captureSession(_ session: Int32, didAdd value: Int32) -> Int32
    func captureSession(_ session: Int32, didChange value: Int32) -> Int32
    func captureSession(_ session: Int32, didUpdate value: Int32) -> Int32
}

/// Drives each of the three requirements and returns the delegate's result.
public class CaptureSessionHarness {
    public init() {}

    public func add(_ observer: CaptureSessionObserver, session: Int32, value: Int32) -> Int32 {
        return observer.captureSession(session, didAdd: value)
    }

    public func change(_ observer: CaptureSessionObserver, session: Int32, value: Int32) -> Int32 {
        return observer.captureSession(session, didChange: value)
    }

    public func update(_ observer: CaptureSessionObserver, session: Int32, value: Int32) -> Int32 {
        return observer.captureSession(session, didUpdate: value)
    }
}

// MARK: - Same-shape NON-protocol overload pair (shared-seam regression lock)
//
// The identical label-only-collision shape on a PLAIN class must NOT hit the protocol
// label-blindness. A class method's primary dedup key is label-INCLUSIVE (the class path
// keeps `label:type` per parameter), so both siblings survive primary dedup; their
// label-erased projected keys still collide, so the second takes the class path's numeric
// suffix (`Configure` / `Configure2`) rather than being dropped. This forward-call fixture
// proves the class-side seam keeps both overloads live — a regression to a label-blind
// class primary key would silently drop the second and fail this test at runtime.
public class OverloadForwardHost {
    public init() {}

    public func configure(_ target: Int32, withMode value: Int32) -> Int32 {
        return value * 2
    }

    public func configure(_ target: Int32, withPriority value: Int32) -> Int32 {
        return value * 3
    }
}
