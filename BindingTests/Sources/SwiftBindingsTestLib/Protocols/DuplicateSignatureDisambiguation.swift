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

// MARK: - Mixed renamed/bare family (family-fold: label-derived naming across a whole base-name group)
//
// A delegate whose `room(...)` requirements are a MIX: two collide on the label-erased C# projection
// (the didAdd/didRemove pair, both `(Int32, Int32)`) and are renamed by the projected-key pass, while a
// third shares the same base name but projects to a DISTINCT C# overload (an extra argument makes it
// `(Int32, Int32, Int32)`) so it never joined the collision group. Left alone it would emit as a bare
// `Room(int, int, int)` overload, reading inconsistently next to its renamed `RoomDidAdd` / `RoomDidRemove`
// siblings. The family-fold rule folds the labels into the type-distinct sibling too, so the whole family
// reads uniformly (`RoomDidFinishWithError`). Slot identity must be preserved: the folded member has its
// OWN reverse-dispatch slot, so a fold that mis-routed it would return the wrong value at runtime here.
public protocol RoomActivityObserver: AnyObject {
    func room(_ room: Int32, didAdd value: Int32) -> Int32
    func room(_ room: Int32, didRemove value: Int32) -> Int32
    func room(_ room: Int32, didFinishWith value: Int32, error code: Int32) -> Int32
}

/// Drives each requirement — including the folded type-distinct sibling — and returns the delegate's
/// result, so the C# test proves each member routes to its own slot after the fold.
public class RoomActivityHarness {
    public init() {}

    public func add(_ observer: RoomActivityObserver, room: Int32, value: Int32) -> Int32 {
        return observer.room(room, didAdd: value)
    }

    public func remove(_ observer: RoomActivityObserver, room: Int32, value: Int32) -> Int32 {
        return observer.room(room, didRemove: value)
    }

    public func finish(_ observer: RoomActivityObserver, room: Int32, value: Int32, code: Int32) -> Int32 {
        return observer.room(room, didFinishWith: value, error: code)
    }
}

// MARK: - Same-shape NON-protocol overload pair (shared-seam regression lock)
//
// The identical label-only-collision shape on a PLAIN class must NOT hit the protocol
// label-blindness. A class method's primary dedup key is label-INCLUSIVE (the class path
// keeps `label:type` per parameter), so both siblings survive primary dedup; their
// label-erased projected keys still collide, so the class path renames BOTH from their own
// labels (`ConfigureWithMode` / `ConfigureWithPriority`) rather than dropping the second. This forward-call fixture
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
