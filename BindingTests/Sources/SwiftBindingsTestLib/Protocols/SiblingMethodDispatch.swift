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

// MARK: - Async/sync effect-overload sibling divergence (Kingfisher regression)
//
// A sync protocol REFINING an async protocol, both declaring a method that shares
// name + params + return TYPE but differs in the `async` effect — exactly
// Kingfisher's `ImageDownloadRequestModifier: AsyncImageDownloadRequestModifier`,
// where both declare `modified(for:)` (the base `async -> URLRequest?`, the
// refinement sync `-> URLRequest?`).
//
// This shape guards TWO opposite failure modes that the EveryProtocol emitter must
// satisfy at once — the `async` axis pulls them in different directions:
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
//   2. Swift side (invalid redeclaration): for a pure-Swift base the emitted EveryProtocol
//      witness drops `async` (a sync candidate satisfies an async requirement), so the
//      async and sync witnesses emit the IDENTICAL `func refineModify(_:) -> Int32`. They
//      must therefore stay in ONE owner/peer group (one owner emits the shared sync witness,
//      the other an empty extension). If the owner/peer grouping key carried `async`, both
//      would emit a body on EveryProtocol -> "invalid redeclaration". So the owner/peer
//      grouping (ComputeMethodEmissionPlans, via GetSwiftMethodFullSignature default) MUST
//      OMIT `async`.
//
// The fix decouples the two: GetSwiftMethodFullSignature carries `async` only in its
// includeAsyncEffect:true (C#-identity) form. This fixture must compile (both gates) AND
// round-trip the sync path.
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
// the async-base interface was the CS1061 locus. Runtime-callable on Mono (no async
// execution). The async-base receiver is compile-gated via the EveryProtocol proxy.
public func callRefineModifySync(_ x: any SyncRefineModifier, _ n: Int32) -> Int32 {
    return x.refineModify(n)
}

// MARK: - Unrelated (non-refining) async/sync same-signature group
//
// Two UNRELATED class-bound protocols (NO refinement between them) declaring the SAME
// method name+params+return TYPE, differing ONLY in the `async` effect. The owner/peer
// grouping omits `async` (a sync witness satisfies an async requirement), so both land
// in ONE EveryProtocol owner/peer group; the ASYNC protocol sorts first ("Async" <
// "Sync") and is the OWNER that emits the shared sync witness body — the peer gets an
// empty extension that borrows that witness.
//
// This complements the refinement shape above (SyncRefineModifier: AsyncRefineModifierBase)
// with the case where the two protocols are INDEPENDENT. It exercises:
//   - owner/peer grouping omitting `async` so an async owner and an unrelated sync peer
//     form ONE group (one shared sync witness body), while the C# sibling-fallback grouping
//     carries `async` so the two stay DISTINCT C# members (MixedFanModifyAsync vs
//     MixedFanModify) — no CS1061 cross-fallback;
//   - the sync-first fan-out branch order (ComputeMethodEmissionPlans) producing a valid
//     mixed-group body where the async owner emits the witness and dispatches through
//     whichever per-protocol vtable a registered proxy populated.
//
// NOTE on the fan-out `self` box: the owner's body boxes `self` as the OWNER's protocol
// type. That box type is behaviorally IMMATERIAL — EveryProtocol unconditionally conforms
// to every sibling here (the empty peer extension borrows this witness), so a box as the
// sync peer also type-checks, and the C# receiver reads only word 0 (the class reference)
// of the existential, never the witness table. So this fixture does NOT — and cannot —
// gate the box's protocol type; owner-box is a clarity/robustness invariant, not a
// correctness requirement. The fixture's value is the grouping/ordering coverage above and
// the sync round-trip below.
public protocol MixedFanAsyncOwner: AnyObject {
    func mixedFanModify(_ n: Int32) async -> Int32
}

public protocol MixedFanSyncPeer: AnyObject {
    func mixedFanModify(_ n: Int32) -> Int32
}

// Driver for the SYNC peer requirement — witnessed by the async OWNER's shared fan-out
// body, so it routes through the owner/peer group under test. Runtime-callable on Mono (no
// async execution); the async-owner receiver path is compile-only via the EveryProtocol proxy.
public func callMixedFanViaSyncPeer(_ x: any MixedFanSyncPeer, _ n: Int32) -> Int32 {
    return x.mixedFanModify(n)
}
