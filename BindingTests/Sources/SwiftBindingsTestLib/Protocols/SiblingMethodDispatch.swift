// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sibling-protocol METHOD dispatch (audit item 1, Bug #2)
//
// The method analog of SiblingPropertyDispatch.swift. A "sibling method group"
// is a set of class-bound protocols that declare the SAME method signature.
// Methods have no accessor sets, so the EveryProtocol emitter picks the
// lexicographically-smaller protocol qualified name as the OWNER, emits the
// dispatch body on its extension, and emits an EMPTY extension for the sibling —
// Swift's cross-extension witness resolution routes the sibling's requirement
// into the owner's body.
//
// Pre-fix bug (Bug #2): the owner's method body hard-coded its OWN global vtable
// (`_owner_vtable.func_x!`). A C# class implementing only the SIBLING (the
// non-owner) populated only the sibling's vtable, leaving the owner's nil. When
// Swift dispatched the shared method through the sibling existential, the owner
// body force-unwrapped its nil function pointer and SIGSEGV'd — or, once any
// owner-conforming proxy had primed the owner vtable globally (the vtables are
// process-wide `fileprivate var`s), the owner branch fired but its receiver
// could not locate this instance's sibling proxy and silently returned the
// dead-impl "" value.
//
// Fix: the owner's body fans out across every sibling's per-protocol vtable,
// dispatching through whichever one the registered proxy populated; and the C#
// receiver for the owner tries this interface first, then each recorded sibling
// interface, so a smaller-sibling proxy is located per-instance even after the
// owner vtable was primed globally. Exactly the EmitMethodImplementation /
// EmitMethodFanOutBody + ComputeSiblingMethodFallbacks path under test.
//
// `SiblingMethodOwner` sorts before `SiblingMethodPeer` ("Owner" < "Peer"), so
// the owner is deterministic and the *Peer existential is the Bug #2 crash path.
// Protocols are `: AnyObject` so the C# proxy is class-backed like the property
// siblings.

public protocol SiblingMethodOwner: AnyObject {
    func siblingMethodValue() -> String
    func siblingMethodEcho(_ n: Int32) -> String
}

public protocol SiblingMethodPeer: AnyObject {
    func siblingMethodValue() -> String
    func siblingMethodEcho(_ n: Int32) -> String
}

// No-argument, value-returning shared method (the cleanest Bug #2 probe — same
// shape as the standalone /tmp crossvtable probe).
public func callSiblingMethodViaOwner(_ x: any SiblingMethodOwner) -> String {
    return x.siblingMethodValue()
}

public func callSiblingMethodViaPeer(_ x: any SiblingMethodPeer) -> String {
    return x.siblingMethodValue()
}

// Argument-bearing shared method: exercises the fan-out body's argument-copy
// emission AND the receiver-side path that must unmarshal the parameter ONCE
// before trying each sibling impl.
public func callSiblingMethodEchoViaOwner(_ x: any SiblingMethodOwner, _ n: Int32) -> String {
    return x.siblingMethodEcho(n)
}

public func callSiblingMethodEchoViaPeer(_ x: any SiblingMethodPeer, _ n: Int32) -> String {
    return x.siblingMethodEcho(n)
}

// MARK: - Sibling-method NAME divergence (audit item 1, Codex r1 Medium)
//
// A same-signature method group where the OWNER also declares a property whose
// base name collides with the method. Swift forbids a property and a zero-arg
// method of the same name (same full name), but PERMITS `var collidingTag` next
// to `func collidingTag(_:)` because the method's full name is `collidingTag(_:)`.
// Both still PascalCase to the SAME C# member `CollidingTag`, so C# interface
// emission renames the method on the OWNER side to free the slot for the property
// (`CollidingTag` property → method becomes e.g. `CollidingTagMethod(n)`). The PEER
// has no such property, so it keeps the plain `CollidingTag(n)`.
//
// The owner's fan-out receiver must therefore resolve the sibling-fallback call
// against the PEER's OWN interface name — reusing the owner's renamed name would
// emit `peerProxy.CollidingTagMethod(...)`, which the Peer interface never defined
// (CS1061/CS1955, caught loud at the compile gate). `SiblingNameOwner` sorts
// before `SiblingNamePeer`, so the owner is the protocol carrying the property.
public protocol SiblingNameOwner: AnyObject {
    var collidingTag: String { get }
    func collidingTag(_ n: Int32) -> String
}

public protocol SiblingNamePeer: AnyObject {
    func collidingTag(_ n: Int32) -> String
}

public func callSiblingNameViaPeer(_ x: any SiblingNamePeer, _ n: Int32) -> String {
    return x.collidingTag(n)
}

public func callSiblingNameViaOwner(_ x: any SiblingNameOwner, _ n: Int32) -> String {
    return x.collidingTag(n)
}
