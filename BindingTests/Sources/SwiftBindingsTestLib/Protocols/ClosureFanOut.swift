// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Same-signature closure-param method fan-out (ABI Coverage Grid — closure corner)
//
// The roadmap "Same-signature closure/async method fan-out gap" Latent: two emittable
// protocols share a method whose signature contains a *dispatchable closure parameter*.
// Because the method signature contains a closure, the EveryProtocol emitter routes its body
// through the closure-method emitter (EmitClosureMethodImplementation) rather than the plain
// method emitter — and only the plain emitter receives the owner/sibling vtable fan-out plan.
// So the owner's closure-method body hard-codes its OWN global vtable. A C# class implementing
// ONLY the non-owner (peer) protocol leaves the owner vtable nil; dispatch through the peer
// existential routes into the owner body, which force-unwraps that nil function pointer.
//
// The sibling-METHOD fan-out for plain and for async/sync effect-overload signatures is already
// fixed + covered (SiblingMethodDispatch.swift). The closure-param shape is the unreached leg.
//
// Shape: ClosureFanOwner < ClosureFanPeer lexically, so Owner is the vtable owner and Peer gets
// the empty stitched extension. `callApplyFactoryViaPeer` dispatches the shared method through
// the PEER existential; a peer-only C# impl must still reach the closure invocation.

public protocol ClosureFanOwner: AnyObject {
    func applyFactory(_ factory: @escaping () -> Int32)
}

public protocol ClosureFanPeer: AnyObject {
    func applyFactory(_ factory: @escaping () -> Int32)
}

/// Drives Swift→C# dispatch of the shared closure-param method through the PEER (non-owner)
/// existential. Passes a Swift closure that records `value` into a class box when invoked, so
/// the round-trip observes that the C# impl actually received and invoked the factory. With the
/// Latent unfixed this traps on the owner's nil vtable force-unwrap before the closure can run.
public func callApplyFactoryViaPeer(_ x: any ClosureFanPeer, _ value: Int32) -> Int32 {
    final class Box { var v: Int32 = -1 }
    let box = Box()
    x.applyFactory {
        box.v = value
        return value
    }
    return box.v
}

/// Control: the same shared method dispatched through the OWNER existential. The owner body
/// reads its own (now-populated) vtable, so this works regardless of the fan-out fix — it pins
/// that the fixture's closure-param dispatch itself is sound.
public func callApplyFactoryViaOwner(_ x: any ClosureFanOwner, _ value: Int32) -> Int32 {
    final class Box { var v: Int32 = -1 }
    let box = Box()
    x.applyFactory {
        box.v = value
        return value
    }
    return box.v
}

// MARK: - Same-signature closure-RETURN method fan-out (ABI Coverage Grid — closure corner)
//
// The closure-RETURN leg of the same Latent. Two emittable protocols share a method whose
// signature *returns* a dispatchable closure (`func makeNotifier() -> () -> Void`). The method
// routes through the EveryProtocol closure-returning emitter
// (EmitDispatchableClosureReturningMethodImplementation) rather than the plain method emitter, so
// — pre-fix — the owner's closure-returning body hard-codes its OWN global vtable. A C# class
// implementing ONLY the non-owner (peer) protocol leaves the owner vtable nil; dispatch through
// the peer existential routes into the owner body, which force-unwraps that nil function pointer.
// The closure-PARAM leg (above) shares the Latent; this leg pins the returning emitter's fan-out.
//
// The returned closure is `() -> Void` because that is the shape the closure-returning materialiser
// dispatches (IsDispatchableClosureReturningMethod gates to a zero-arg void return; a non-void
// closure return like `() -> Int32` is a separate, unimplemented capability — see the manifest's
// documented-absence note). The value round-trip oracle is therefore C#-side: the returned Action
// records into the impl when Swift invokes it.
//
// Shape: ClosureRetFanOwner < ClosureRetFanPeer lexically, so Owner is the vtable owner and Peer
// gets the empty stitched extension. `callMakeNotifierViaPeer` dispatches the shared method through
// the PEER existential, then invokes the returned closure — a peer-only C# impl must reach it.

public protocol ClosureRetFanOwner: AnyObject {
    func makeNotifier() -> () -> Void
}

public protocol ClosureRetFanPeer: AnyObject {
    func makeNotifier() -> () -> Void
}

/// Drives Swift→C# dispatch of the shared closure-returning method through the PEER (non-owner)
/// existential, then invokes the returned closure. With the Latent unfixed this traps on the
/// owner's nil vtable force-unwrap before the returned closure can be obtained. The C# impl's
/// returned Action records the round-trip value when invoked here.
public func callMakeNotifierViaPeer(_ x: any ClosureRetFanPeer) {
    let notifier = x.makeNotifier()
    notifier()
}

/// Control: the same shared method dispatched through the OWNER existential. The owner body reads
/// its own (now-populated) vtable, so this works regardless of the fan-out fix — it pins that the
/// fixture's closure-return dispatch itself is sound.
public func callMakeNotifierViaOwner(_ x: any ClosureRetFanOwner) {
    let notifier = x.makeNotifier()
    notifier()
}
