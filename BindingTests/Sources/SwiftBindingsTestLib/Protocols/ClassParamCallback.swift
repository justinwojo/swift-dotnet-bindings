// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Class-parameter reverse-callback regression (issue #40)
//
// When Swift calls back into a C# protocol implementation with a method whose
// parameter is a Swift *class* instance, the generated proxy receiver used to
// marshal it with a naive `Unsafe.Read<T>` — reinterpreting the Swift heap
// pointer as a managed reference, which crashes (SIGSEGV) the first time the
// reference is used. Strings dodged this via a special case; concrete Swift
// classes had no branch and fell through to the broken fallback.
//
// This fixture reproduces both the pure-Swift and the `@objc … : NSObject`
// payload variants. The `@objc` variant is the @objc:NSObject reverse-dispatch
// repro shape (issue #40) and exercises the ObjC-aware retain (`swift_unknownObjectRetain`)
// half of the fix — native-only `swift_retain` is a no-op / over-release on an
// NSObject subclass.
//
// All payload classes feed the shared LifetimeTracker counters
// (`recordTrackedAllocation` / `recordTrackedDeallocation`, defined in
// Lifetime/OwnershipTests.swift) so the C# side can assert ARC balance, not just
// the absence of a crash.

// MARK: - Pure-Swift class payload

/// A plain Swift class carried as a reverse-callback parameter. Reading `code`
/// or `label` after the callback is the operation that crashed pre-fix.
public class ClassParamPayload {
    public let code: Int32
    public let label: String

    public init(code: Int32, label: String) {
        self.code = code
        self.label = label
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Class-bound protocol whose methods receive a Swift-class parameter. The
/// generated `IClassParamReceiver` is what the C# test implements; Swift calls
/// back into it through the generated proxy receiver.
public protocol ClassParamReceiver: AnyObject {
    /// Plain class parameter — the core repro.
    func didReceive(_ payload: ClassParamPayload)
    /// Optional<class> parameter — separate marshalling branch.
    func didReceiveOptional(_ payload: ClassParamPayload?)
}

/// Synchronous driver: constructs a payload Swift-side and calls back into the
/// receiver on the same thread, so the C# test needs no runloop pumping.
public class ClassParamDriver {
    public init() {}

    /// Drive the plain-class callback.
    public func drive(_ receiver: ClassParamReceiver, code: Int32, label: String) {
        let payload = ClassParamPayload(code: code, label: label)
        receiver.didReceive(payload)
    }

    /// Drive the Optional<class> callback with a non-nil payload.
    public func driveOptional(_ receiver: ClassParamReceiver, code: Int32, label: String) {
        let payload = ClassParamPayload(code: code, label: label)
        receiver.didReceiveOptional(payload)
    }

    /// Drive the Optional<class> callback with nil.
    public func driveOptionalNil(_ receiver: ClassParamReceiver) {
        receiver.didReceiveOptional(nil)
    }
}

// MARK: - @objc : NSObject class payload (the @objc:NSObject reverse-dispatch shape)

/// ObjC-rooted class payload — `@objc … : NSObject`. This is the variant that
/// exercises the ObjC-aware retain fix: the copy-out path must `swift_unknownObjectRetain`
/// (isa-dispatching), not native-only `swift_retain`, which is a no-op /
/// over-release on an NSObject subclass.
@objc public class ObjCClassParamPayload: NSObject {
    public let code: Int32
    public let label: String

    public init(code: Int32, label: String) {
        self.code = code
        self.label = label
        recordTrackedAllocation()
        super.init()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Class-bound protocol receiving the ObjC-rooted payload.
public protocol ObjCClassParamReceiver: AnyObject {
    func didReceiveObjC(_ payload: ObjCClassParamPayload)
    func didReceiveObjCOptional(_ payload: ObjCClassParamPayload?)
}

/// Synchronous driver for the ObjC-rooted payload callbacks.
public class ObjCClassParamDriver {
    public init() {}

    public func drive(_ receiver: ObjCClassParamReceiver, code: Int32, label: String) {
        let payload = ObjCClassParamPayload(code: code, label: label)
        receiver.didReceiveObjC(payload)
    }

    public func driveOptional(_ receiver: ObjCClassParamReceiver, code: Int32, label: String) {
        let payload = ObjCClassParamPayload(code: code, label: label)
        receiver.didReceiveObjCOptional(payload)
    }

    public func driveOptionalNil(_ receiver: ObjCClassParamReceiver) {
        receiver.didReceiveObjCOptional(nil)
    }

    /// Hands the callback the payload the CALLER supplied, instead of one this driver
    /// minted. This is the delegate shape a consumer actually writes — the object the
    /// C# side created is handed straight back to it through the callback — and it is
    /// the only way to reach the managed-peer-reuse branch: a payload minted inside
    /// Swift has no managed peer yet, so the callback legitimately constructs one.
    public func driveWithCallerPayload(_ receiver: ObjCClassParamReceiver, payload: ObjCClassParamPayload) {
        receiver.didReceiveObjC(payload)
    }
}

// MARK: - Managed-peer identity for @objc:NSObject classes
//
// An `@objc … : NSObject` Swift class projects onto an NSObject-derived C# base, and a
// native NSObject may have at most ONE managed peer — the Apple bindings keep a
// handle→peer map. Every path that turns a raw handle back into a wrapper (the
// reverse-dispatch receiver above, and the return-value marshalling below) therefore has
// to hand back the peer that already exists rather than minting a second one over the
// same native object.
//
// `driveWithCallerPayload` covers the callback direction; `echoObjCPayload` covers the
// return direction through the same `NewFromPayload` seam.

/// Returns the very same instance it was handed. The C# caller passes an object it
/// created, so a correct return-direction marshal gives back the identical managed peer.
public func echoObjCPayload(_ payload: ObjCClassParamPayload) -> ObjCClassParamPayload {
    return payload
}

// MARK: - Return-direction @objc coverage
//
// The same ObjC-aware-retain concern lives in the *return-value* copy-out paths.
// Two `Arc.Retain` sites were upgraded to the isa-dispatching `Arc.UnknownObjectRetain`:
//   • `SwiftMarshal.ExtractCopiedValue`   — the Optional/Result payload copy-out.
//   • `SwiftMarshal.ExtractCopiedElement` — the per-tuple-element copy-out.
//
// Reaching those two paths with an `@objc:NSObject` payload (so native
// `swift_retain` vs `swift_unknownObjectRetain` actually diverges) requires the
// RIGHT carriers — established empirically against the generated bindings:
//   • `ExtractCopiedValue`  ← `Result<class, Error>` read via `SwiftResult.Success`
//                             (`SwiftResult.ExtractPayloadValue` → `ExtractCopiedValue`).
//   • `ExtractCopiedElement`← an `Optional<(class, scalar)>` whose `.Some` tuple is
//                             marshalled through the runtime `MarshalTupleFromSwift`
//                             (→ `MarshalElementFromSwiftUnsafe` → `ExtractCopiedElement`).
// The `stashShared…` probes below carry those shapes and hold the payload in a Swift
// global so an over/under-retain shows up as the global's live count diverging from 1.
//
// The three `make…` returns below do NOT reach `ExtractCopiedValue`/`ExtractCopiedElement`.
// They are kept as honest independent coverage of the paths they DO exercise:
//   • `makeOptionalObjCPayload`  → an `Optional<@objc>` return marshalled inline as
//        `result == IntPtr.Zero ? null : GetINativeObject<T>(result, true)` (the adopting
//        return path). This path ADOPTS the Swift `passRetained` +1 via owns:true, so the
//        managed peer releases exactly once on Dispose/finalize — the "Fix A" over-retain
//        (bare `GetNSObject`, owns:false, adds an unbalanced second +1) is fixed; see
//        the protocol-proxy class-param receiver fix that corrects the over-retain.
//   • `makeObjCPayloadCodeTuple` → a *non-optional* `(@objc, scalar)` tuple, which the
//        emitter UNROLLS per element (`_tupleMetaPtr->GetElementOffset` + direct
//        `MarshalFromSwift`) — it does NOT go through `MarshalTupleFromSwift`, so it does
//        NOT reach `ExtractCopiedElement`.
//   • `makeObjCPayloadArray`     → `SwiftArray.Get`, whose subscript getter returns an
//        already-owned (+1) element adopted via `MarshalFromSwift` with no extra retain.

/// Strong global holding the SAME `ObjCClassParamPayload` returned through the Result /
/// Optional-tuple carriers below, so the C# extraction's over/under-retain is observable
/// as the global's live count diverging from 1 while the global is non-nil — synchronous,
/// no GC timing involved. Mirrors `_sharedExtractionRef` in LeakDetection.swift but with
/// the `@objc:NSObject` payload that exercises the ObjC-aware retain path.
private var _sharedObjCExtractionRef: ObjCClassParamPayload?

/// Stashes an `@objc:NSObject` payload in the global, returns it through
/// `Result<ObjCClassParamPayload, TrackedRefError>` (`.success`). The C# `.Success` getter
/// copies it out via `SwiftResult.ExtractPayloadValue` → `SwiftMarshal.ExtractCopiedValue`.
/// For an NSObject subclass that copy-out MUST `swift_unknownObjectRetain`, not native
/// `swift_retain`.
public func stashSharedObjCRefAndReturnResult(code: Int32, label: String) -> Result<ObjCClassParamPayload, TrackedRefError> {
    let payload = ObjCClassParamPayload(code: code, label: label)
    _sharedObjCExtractionRef = payload
    return .success(payload)
}

/// `Optional<(ObjCClassParamPayload, Int32)>` companion: the class element is copied out of
/// a borrowed tuple slot via `MarshalTupleFromSwift` → `MarshalElementFromSwiftUnsafe` →
/// `SwiftMarshal.ExtractCopiedElement`. The trailing scalar forces a real 2-element tuple.
/// Wrapping the tuple in `Optional` routes it through the runtime tuple marshaller (unlike a
/// bare tuple return, which the emitter unrolls per element).
public func stashSharedObjCRefAndReturnOptionalTuple(code: Int32) -> (ObjCClassParamPayload, Int32)? {
    let payload = ObjCClassParamPayload(code: code, label: "tuple")
    _sharedObjCExtractionRef = payload
    return (payload, code)
}

/// Clears the shared global so the C# side can assert the extracted-copy lifecycle:
/// live == 1 while the global owns the ref, live == 0 once cleared.
public func clearSharedObjCExtractionRef() {
    _sharedObjCExtractionRef = nil
}

/// Returns an `Optional<@objc>` marshalled inline as `result == IntPtr.Zero ? null :
/// GetINativeObject<T>(result, true)` — the adopting return path (see the "Fix A" note above).
/// Independent coverage that the path compiles, reads a correct value, and balances ARC.
public func makeOptionalObjCPayload(code: Int32, label: String) -> ObjCClassParamPayload? {
    return ObjCClassParamPayload(code: code, label: label)
}

/// Returns nil through the same Optional return type.
public func makeOptionalObjCPayloadNil() -> ObjCClassParamPayload? {
    return nil
}

/// Returns a *non-optional* `(@objc, scalar)` tuple. The emitter UNROLLS this per element
/// (direct `MarshalFromSwift` at each element offset) — it does NOT reach
/// `ExtractCopiedElement`. Kept as independent coverage of the unrolled tuple path.
public func makeObjCPayloadCodeTuple(code: Int32, label: String) -> (ObjCClassParamPayload, Int32) {
    return (ObjCClassParamPayload(code: code, label: label), code)
}

/// Returns ObjC-rooted payloads in an array. Covers the `SwiftArray.Get` element path
/// (NOT `ExtractCopiedElement`); kept as independent array-path coverage.
public func makeObjCPayloadArray(count: Int32) -> [ObjCClassParamPayload] {
    return (0..<count).map { ObjCClassParamPayload(code: $0, label: "item-\($0)") }
}
