// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Concrete-specialization argument/receiver lease fixture
//
// The concrete-specialization ("CSM") overloads forward a receiver and every native-backed
// argument to a `@_cdecl` wrapper. Those handles must be LEASED for the duration of the call:
// without a lease, a `Dispose()` on another thread frees the native storage while the Swift body
// is still dereferencing it, and an already-disposed argument is dereferenced instead of being
// rejected. The ordinary (non-specialized) method emitter gets this for free by typing the
// P/Invoke parameter as a `SafeHandle`, which makes the `LibraryImport` marshaller bracket the
// call with `DangerousAddRef`/`DangerousRelease` and throw `ObjectDisposedException` up front.
//
// These fixtures put a test-controlled gate INSIDE the specialized Swift body so a test can park
// the call in native code and dispose its arguments from another thread — the only way to observe
// the lease rather than a `GC.KeepAlive`-shaped approximation of it. They cover both self shapes
// (struct receiver, class receiver) and the argument categories the specializer marshals through a
// payload: a class conformer, a non-frozen-struct conformer, and a concrete class parameter.

// MARK: Test-controlled gate

private let leaseEnteredSem = DispatchSemaphore(value: 0)
private let leaseReleaseSem = DispatchSemaphore(value: 0)
private let leaseLock = NSLock()
private var leaseGateArmed = false
private var leaseEntryCount: Int32 = 0

/// Called from inside every gated specialized body. Records that native code was actually entered
/// (so a test can prove a rejected call never ran) and, when armed, parks until the test releases
/// it. The wait is bounded so a failing test surfaces as an assertion rather than a hung app.
private func leaseGateHold() {
    leaseLock.lock()
    leaseEntryCount += 1
    let armed = leaseGateArmed
    leaseLock.unlock()
    guard armed else { return }
    leaseEnteredSem.signal()
    _ = leaseReleaseSem.wait(timeout: .now() + .seconds(30))
}

/// Arms the gate: the next gated call parks inside native code until `LeaseGateRelease`.
@_cdecl("SwiftBindingsTestLib_LeaseGateArm")
public func leaseGateArm() {
    leaseLock.lock()
    leaseGateArmed = true
    leaseLock.unlock()
}

/// Disarms the gate, zeroes the entry counter and drains both semaphores.
@_cdecl("SwiftBindingsTestLib_LeaseGateReset")
public func leaseGateReset() {
    leaseLock.lock()
    leaseGateArmed = false
    leaseEntryCount = 0
    leaseLock.unlock()
    while leaseEnteredSem.wait(timeout: .now()) == .success {}
    while leaseReleaseSem.wait(timeout: .now()) == .success {}
}

/// Number of gated bodies entered since the last reset.
@_cdecl("SwiftBindingsTestLib_LeaseGateEntryCount")
public func leaseGateEntryCount() -> Int32 {
    leaseLock.lock()
    defer { leaseLock.unlock() }
    return leaseEntryCount
}

/// Blocks the CALLING thread until a gated call has entered native code. Returns 1 on entry,
/// 0 on timeout.
@_cdecl("SwiftBindingsTestLib_LeaseGateAwaitEntry")
public func leaseGateAwaitEntry(_ timeoutMilliseconds: Int32) -> Int32 {
    let deadline = DispatchTime.now() + .milliseconds(Int(timeoutMilliseconds))
    return leaseEnteredSem.wait(timeout: deadline) == .success ? 1 : 0
}

/// Lets a parked gated call proceed.
@_cdecl("SwiftBindingsTestLib_LeaseGateRelease")
public func leaseGateRelease() {
    leaseReleaseSem.signal()
}

// MARK: Specialized surface

/// Constraint protocol for the specialized members below.
public protocol LeasedMaterial {
    var material: String { get }
}

/// Class conformer — the specializer marshals it through its ARC payload handle.
public final class LeasedRefMaterial: LeasedMaterial {
    public let token: String
    public init(token: String) { self.token = token }
    public var material: String { "ref:\(token)" }
}

/// Non-frozen struct conformer — projected as a C# class over an opaque payload handle, the
/// sibling payload category to the class conformer above.
public struct LeasedValueMaterial: LeasedMaterial {
    public let token: String
    public init(token: String) { self.token = token }
    public var material: String { "value:\(token)" }
}

/// A CONCRETE (non-specializable) class parameter riding alongside the specializable one. It takes
/// the specializer's plain payload-handle parameter arm rather than the conformer arm, so it must
/// be leased by the same mechanism.
public final class LeaseContext {
    public let label: String
    public init(label: String) { self.label = label }
}

/// STRUCT receiver: the specialized overload forwards `self` from the struct's own payload.
@frozen
public struct LeaseProbe {
    public let realm: String
    public init(realm: String) { self.realm = realm }

    /// Gated specialized method carrying a conformer argument AND a concrete class argument.
    public func consumeGated<M: LeasedMaterial>(_ material: M, context: LeaseContext) -> String {
        leaseGateHold()
        return "\(realm)|\(context.label)|\(material.material)"
    }
}

/// NON-frozen struct host with a generic initializer, so its specialized `From{Conformer}`
/// factories return through the indirect-result buffer that the RETURNED handle adopts —
/// the ownership-transfer shape, the one return arm whose buffer is not reclaimed by the
/// caller on the success path. Everything between the buffer's allocation and that handoff can
/// still throw without a handle ever taking it: most importantly the `SafeHandle` marshaller,
/// which rejects a disposed argument before native code is entered. The gate call lets a test
/// prove the rejection happened on that side of the boundary.
public struct LeasedResultBox {
    public let descriptor: String

    public init<M: LeasedMaterial>(sealing material: M) {
        leaseGateHold()
        self.descriptor = "boxed[\(material.material)]"
    }
}

/// CLASS receiver: the specialized overload forwards `self` from the class's ARC payload.
public final class LeaseProbeRef {
    public let realm: String
    public init(realm: String) { self.realm = realm }

    /// Gated specialized method on a class receiver — exercises the class self arm.
    public func consumeGated<M: LeasedMaterial>(_ material: M) -> String {
        leaseGateHold()
        return "\(realm)|\(material.material)"
    }
}
