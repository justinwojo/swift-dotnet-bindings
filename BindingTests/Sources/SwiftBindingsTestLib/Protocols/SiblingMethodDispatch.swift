// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sibling-protocol METHOD dispatch
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

// MARK: - Sibling-method NAME divergence
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

// MARK: - Async/sync effect-overload sibling divergence
//
// A sync protocol REFINING an async protocol, both declaring a method that shares
// name + params + return TYPE but differs in the `async` effect — `refineModify(_:)`
// (the base `async`, the refinement sync, same Int32 return type).
//
// This shape guards TWO failure modes that the EveryProtocol emitter must satisfy at
// once — the `async` axis pulls the C# identity and the Swift witness in step:
//
//   1. C# side (CS1061): the async requirement projects to a DIFFERENT C# member than
//      the sync one (`RefineModifyAsync(int, CancellationToken) -> Task<int>` vs
//      `RefineModify(int) -> int`). If the C# receiver sibling-fallback grouping omits
//      `async`, the two collapse into one group, the sync receiver treats the async-base
//      interface as a sibling, and emits `impl.RefineModify(...)` against an interface
//      that only declares `RefineModifyAsync` -> CS1061. So the C# fallback grouping
//      (ComputeSiblingMethodFallbacks, via GetSwiftMethodFullSignature includeAsyncEffect:true,
//      and GetMethodSiblingMapKey) MUST carry `async`.
//
//   2. Swift side (distinct effect overloads, NOT redeclaration): the async base satisfies
//      the real reverse-async witness predicate (S13 Pillar C — non-throwing `async`, Int32
//      return, arity 1), so its EveryProtocol witness KEEPS `async` and emits a genuine
//      `func refineModify(_:) async -> Int32` that suspends on `withCheckedContinuation` and
//      hands the continuation back to C#. The sync refinement emits `func refineModify(_:) ->
//      Int32`. Those are two DISTINCT effect overloads — valid Swift, no redeclaration — so
//      the owner/peer grouping key carries `async` for the real-async witness
//      (ComputeMethodEmissionPlans, via GetSwiftMethodFullSignature
//      includeAsyncEffect:EmitsRealAsyncWitness) and omits it for the sync witness, landing
//      them in DISTINCT owner groups that each emit their own body. (The legacy blocking
//      witness DROPPED `async` and shared one group to dodge the redeclaration; that path now
//      serves only async methods the real-async predicate rejects — closure params, generics,
//      non-primitive return, arity > 4.)
//
// GetSwiftMethodFullSignature carries `async` in its includeAsyncEffect form (C#-identity
// always; owner/peer grouping for real-async witnesses). This fixture must compile (both
// gates) AND round-trip BOTH the sync path and the real reverse-async base path.
//
// (`throws` is deliberately NOT in EITHER key: a non-throwing witness satisfies a
// throwing requirement in Swift, so throwing/non-throwing same-signature methods
// share a witness — the nonThrowingOverrides mechanism — and must stay grouped.)
public protocol AsyncRefineModifierBase: AnyObject {
    func refineModify(_ n: Int32) async -> Int32
}

public protocol SyncRefineModifier: AsyncRefineModifierBase {
    func refineModify(_ n: Int32) -> Int32
}

// Driver for the SYNC requirement — the receiver site whose sibling fan-out into
// the async-base interface was the CS1061 locus.
public func callRefineModifySync(_ x: any SyncRefineModifier, _ n: Int32) -> Int32 {
    return x.refineModify(n)
}

// Driver for the async BASE requirement — routes through the real reverse-async witness,
// genuinely suspending the Swift task until C# resumes the boxed continuation. Pass an
// instance whose C# type conforms to the async base (a SyncRefineModifier impl also conforms,
// since the protocol refines the async base), proving the async-base witness dispatches
// independently of the sync refinement's slot.
public func callRefineModifyViaAsyncBase(_ x: any AsyncRefineModifierBase, _ n: Int32) async -> Int32 {
    return await x.refineModify(n)
}

// MARK: - Unrelated (non-refining) async/sync same-signature group
//
// Two UNRELATED class-bound protocols (NO refinement between them) declaring the SAME
// method name+params+return TYPE, differing ONLY in the `async` effect. The async
// requirement (`MixedFanAsyncOwner.mixedFanModify async -> Int32`) satisfies the real
// reverse-async witness predicate (S13 Pillar C), so its EveryProtocol witness KEEPS
// `async` and emits a genuine `func mixedFanModify(_:) async -> Int32`. The sync peer emits
// `func mixedFanModify(_:) -> Int32`. Because the owner/peer grouping key carries `async`
// for the real-async witness (includeAsyncEffect:EmitsRealAsyncWitness) and omits it for the
// sync peer, the two protocols land in DISTINCT owner groups — each emitting its OWN
// effect-overload witness body. They no longer share one group: the witnesses are two
// distinct effect overloads on EveryProtocol (valid Swift, no redeclaration), and a C# class
// implementing only one populates only that protocol's vtable, dispatched by that protocol's
// own witness.
//
// This complements the refinement shape above (SyncRefineModifier: AsyncRefineModifierBase)
// with the case where the two protocols are INDEPENDENT. It exercises:
//   - owner/peer grouping carrying `async` for the real-async witness so an async owner and
//     an unrelated sync peer form TWO distinct groups (two distinct effect-overload witness
//     bodies), while the C# sibling-fallback grouping likewise carries `async` so the two
//     stay DISTINCT C# members (MixedFanModifyAsync vs MixedFanModify) — no CS1061 cross-fallback;
//   - both effect-overload witnesses coexisting on EveryProtocol and each round-tripping at
//     runtime: the sync peer through its sync vtable slot, the async owner through the real
//     reverse-async witness (suspend + continuation handoff to C#).
public protocol MixedFanAsyncOwner: AnyObject {
    func mixedFanModify(_ n: Int32) async -> Int32
}

public protocol MixedFanSyncPeer: AnyObject {
    func mixedFanModify(_ n: Int32) -> Int32
}

// Driver for the SYNC peer requirement — routes through the sync peer's own witness body.
public func callMixedFanViaSyncPeer(_ x: any MixedFanSyncPeer, _ n: Int32) -> Int32 {
    return x.mixedFanModify(n)
}

// Driver for the async OWNER requirement — routes through the real reverse-async witness,
// genuinely suspending until C# resumes the boxed continuation. Proves the async owner's
// distinct effect-overload witness dispatches independently of the unrelated sync peer.
public func callMixedFanViaAsyncOwner(_ x: any MixedFanAsyncOwner, _ n: Int32) async -> Int32 {
    return await x.mixedFanModify(n)
}
