// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional<class> closure-argument ownership arbiter
//
// Each method here takes a closure whose single parameter is `Optional<class>`, invoked from Swift
// back into the C# closure. The per-argument conversion that marshals each Swift payload into its C#
// type runs in `ClosureEmitter.GetInvokeArgExpression` for BOTH the `@_cdecl` and the CallConvSwift
// closure-callback shapes — the closure's calling convention selects how the closure is *invoked*, but
// the argument lowering is the same code either way. This fixture pins the OWNERSHIP convergence:
// every closure-argument site now routes through the single shared narrow predicate
// `ClosureHandler.IsOptionalReferenceArg` (= `Optional<T>` with a true-reference inner: a Swift class,
// ObjC-rooted class, or ObjC-bridged class).
//
// For an `Optional<class>` inner the convergence routes the +1 marshal consistently: a pure-Swift
// class via `MarshalBorrowedClassFromSwift`, and an ObjC-rooted (`@objc … : NSObject`) class via
// `MarshalCallbackArg` (its `Kind == Class` upgrade) — the SAME isa-aware +1 the non-optional
// reference arm and the MethodClosureBridge path use. Before the convergence the ObjC-rooted arm went
// through `FormatObjCBridgeCall` (`GetNSObject`); both round-trip and balance ARC for such a
// dual-natured NSObject peer, so this is a consistency convergence — and the first regression coverage
// of either ObjC-rooted or pure-Swift `Optional<class>` closure arguments. Both closures are
// `@_cdecl`-compatible because their inner is a generator-bound class with Swift metadata.
//
// NOT HERE — `Optional<ObjC-bridgeable VALUE type>` closure arguments (e.g. `(URL?) -> Void`). A
// closure's native slot carries such an inner by its Swift VALUE representation, not as an object
// pointer, so it is not a reference inner and the narrow `IsOptionalReferenceArg` predicate this
// arbiter pins does not admit it. The closure adapter bridges those values to their Foundation peer
// with `as AnyObject` on a separate arm; they are fixtured in Closures/BridgeableValueClosureArgs.swift.
//
// Payloads feed the shared LifetimeTracker counters (`recordTrackedAllocation` /
// `recordTrackedDeallocation`, defined in Lifetime/OwnershipTests.swift) so the C# side can assert ARC
// balance, not merely the absence of a crash. The Swift wrapper passes the inner as `passUnretained`
// (+0 borrow) and the C# marshal takes its own +1 — a matched pair; flipping the Swift side to
// `passRetained` would leak one retain per callback arg.

/// ObjC-rooted (`@objc … : NSObject`) class payload carried as an optional
/// closure argument. Reading `.code` / `.label` after the callback is the
/// operation that would dereference a mis-marshalled handle pre-fix.
@objc public class ClosureOptionalObjCPayload: NSObject {
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

/// Pure-Swift (non-rooted) class payload — the control arm that already worked
/// (an `IsClassType` inner routes to `MarshalBorrowedClassFromSwift`).
public class ClosureOptionalSwiftPayload {
    public let code: Int32

    public init(code: Int32) {
        self.code = code
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Drives optional-reference closure arguments synchronously, on the calling
/// thread, so the C# test needs no runloop pumping for the round-trip itself.
public class OptionalReferenceClosureArbiter {
    public init() {}

    /// Invokes the C# closure with a non-nil / nil `Optional<@objc:NSObject>` (ownership axis).
    public func emitObjC(present: Bool, completion: @escaping (ClosureOptionalObjCPayload?) -> Void) {
        if present {
            completion(ClosureOptionalObjCPayload(code: 7, label: "rooted"))
        } else {
            completion(nil)
        }
    }

    /// Control: pure-Swift `Optional<class>` closure argument (ownership axis).
    public func emitSwift(present: Bool, completion: @escaping (ClosureOptionalSwiftPayload?) -> Void) {
        if present {
            completion(ClosureOptionalSwiftPayload(code: 11))
        } else {
            completion(nil)
        }
    }
}
