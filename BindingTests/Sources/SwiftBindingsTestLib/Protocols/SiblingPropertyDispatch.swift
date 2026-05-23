// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sibling-protocol property dispatch
//
// A "sibling protocol group" is a set of class-bound protocols that declare
// the same property name+type with different accessor sets. The EveryProtocol
// emitter picks the protocol with the fattest accessor set as the OWNER and
// emits the dispatch body on its extension; sibling protocols emit empty
// extensions and rely on Swift's cross-extension witness resolution to route
// inherited calls into the owner's body.
//
// Pre-fix bug: the owner's body always called its OWN vtable, even when the
// dispatched-through proxy populated a SIBLING's vtable. A C# class that
// implemented only the smaller sibling left the owner's vtable nil; the
// owner's body force-unwrapped that nil function pointer and crashed
// (SIGSEGV).
//
// These fixtures exercise:
//   - 2-sibling group: SiblingNamed (get) + SiblingMutableNamed (get set)
//   - 3-sibling group: SiblingTagged (get) + SiblingMutableTagged (get set)
//                      + SiblingMutableTaggedAlt (get set)
// Trigger functions read/write the property through each sibling existential
// so the test class can exercise every sibling × accessor combination.
//
// Protocols are `: AnyObject` so the setter can be invoked through a `let`
// existential without `inout` plumbing on the binding boundary.

// MARK: - 2-sibling group

public protocol SiblingNamed: AnyObject {
    var siblingName: String { get }
}

public protocol SiblingMutableNamed: AnyObject {
    var siblingName: String { get set }
}

public func readSiblingNameViaGet(_ x: any SiblingNamed) -> String {
    return x.siblingName
}

public func readSiblingNameViaGetSet(_ x: any SiblingMutableNamed) -> String {
    return x.siblingName
}

public func writeSiblingNameViaGetSet(_ x: any SiblingMutableNamed, _ v: String) {
    x.siblingName = v
}

// MARK: - 3-sibling group (two siblings carry setters)

public protocol SiblingTagged: AnyObject {
    var siblingTag: String { get }
}

public protocol SiblingMutableTagged: AnyObject {
    var siblingTag: String { get set }
}

public protocol SiblingMutableTaggedAlt: AnyObject {
    var siblingTag: String { get set }
}

public func readSiblingTagViaGet(_ x: any SiblingTagged) -> String {
    return x.siblingTag
}

public func readSiblingTagViaGetSet(_ x: any SiblingMutableTagged) -> String {
    return x.siblingTag
}

public func readSiblingTagViaGetSetAlt(_ x: any SiblingMutableTaggedAlt) -> String {
    return x.siblingTag
}

public func writeSiblingTagViaGetSet(_ x: any SiblingMutableTagged, _ v: String) {
    x.siblingTag = v
}

public func writeSiblingTagViaGetSetAlt(_ x: any SiblingMutableTaggedAlt, _ v: String) {
    x.siblingTag = v
}

// MARK: - Inheritance variant
//
// Child refines a get-only requirement from Parent into get+set. The owner
// (Child) emits the dispatch body; Parent's empty extension routes inherited
// dispatch into Child's body. Probes whether ABI parsing duplicates inherited
// PropertyDecls into the child protocol's .Properties (required for the
// sibling group to be detected).

public protocol SiblingInheritedParent: AnyObject {
    var inheritedSiblingValue: String { get }
}

public protocol SiblingInheritedChild: SiblingInheritedParent {
    var inheritedSiblingValue: String { get set }
}

public func readInheritedSiblingViaParent(_ x: any SiblingInheritedParent) -> String {
    return x.inheritedSiblingValue
}

public func readInheritedSiblingViaChild(_ x: any SiblingInheritedChild) -> String {
    return x.inheritedSiblingValue
}

public func writeInheritedSiblingViaChild(_ x: any SiblingInheritedChild, _ v: String) {
    x.inheritedSiblingValue = v
}

// MARK: - Closure-property sibling group
//
// Two class-bound protocols declare the same Optional<() -> Void> property
// with different accessor sets. Same shape as the value-typed sibling fix but
// the emission path is EveryProtocolEmitter.EmitDispatchableClosurePropertyImplementation
// (16-byte (fnPtr, ctxPtr) buffer marshalling), which prior to this fix took
// only the owner protocol/vtable and force-unwrapped its own nil pointer when
// dispatched through a smaller sibling.

public protocol SiblingClosureProperty: AnyObject {
    var siblingClosure: (() -> Void)? { get }
}

public protocol SiblingMutableClosureProperty: AnyObject {
    var siblingClosure: (() -> Void)? { get set }
}

public func invokeSiblingClosureViaGet(_ x: any SiblingClosureProperty) {
    x.siblingClosure?()
}

public func invokeSiblingClosureViaGetSet(_ x: any SiblingMutableClosureProperty) {
    x.siblingClosure?()
}

public func setSiblingClosureViaGetSet(_ x: any SiblingMutableClosureProperty, _ closure: @escaping () -> Void) {
    x.siblingClosure = closure
}

// MARK: - Subscript sibling group
//
// Two class-bound protocols declare the same subscript signature with different
// accessor sets. Same shape as the value-typed property sibling fix but the
// emission path is EveryProtocolEmitter.EmitSubscriptImplementation. Without the
// sibling fan-out, a C# impl that only implements the smaller sibling leaves the
// owner's subscript vtable nil and the force-unwrapped pointer SIGSEGVs.

public protocol SiblingIndexed: AnyObject {
    subscript(_ key: Int) -> String { get }
}

public protocol SiblingMutableIndexed: AnyObject {
    subscript(_ key: Int) -> String { get set }
}

public func readSiblingIndexedViaGet(_ x: any SiblingIndexed, _ key: Int) -> String {
    return x[key]
}

public func readSiblingIndexedViaGetSet(_ x: any SiblingMutableIndexed, _ key: Int) -> String {
    return x[key]
}

public func writeSiblingIndexedViaGetSet(_ x: any SiblingMutableIndexed, _ key: Int, _ value: String) {
    x[key] = value
}

// MARK: - Subscript sibling group, divergent argument labels
//
// Same index parameter type (Int) and return type (String) as the SiblingIndexed
// pair above, but the external argument labels differ. Swift treats `subscript(at:)`
// and `subscript(by:)` as distinct witnesses, so they MUST land in separate sibling
// groups; otherwise the owner emits a single labeled body that doesn't satisfy
// either protocol's witness signature for the other label.

public protocol SiblingLabelAt: AnyObject {
    subscript(at index: Int) -> String { get }
}

public protocol SiblingLabelBy: AnyObject {
    subscript(by index: Int) -> String { get set }
}

public func readSiblingLabelAt(_ x: any SiblingLabelAt, _ index: Int) -> String {
    return x[at: index]
}

public func readSiblingLabelBy(_ x: any SiblingLabelBy, _ index: Int) -> String {
    return x[by: index]
}

public func writeSiblingLabelBy(_ x: any SiblingLabelBy, _ index: Int, _ value: String) {
    x[by: index] = value
}

// MARK: - Subscript external-label edge cases
//
// 1. Keyword-as-label: `default` is a Swift keyword, so a robust emitter must
//    backtick-escape it when rendered as an external label. Without escaping,
//    the Swift wrapper would fail to compile: `subscript(default arg0: Int)`.
// 2. `indexN`-as-label: a user-written external label that literally spells
//    `index0` collides with the parser's synthetic placeholder for unlabeled
//    positions. The emitter must distinguish via the parser-set flag, NOT a
//    pattern match on the name; otherwise the witness would be emitted as
//    `subscript(_ arg0: Int)` and silently fail to satisfy the protocol.

public protocol SiblingLabelKeyword: AnyObject {
    subscript(`default` index: Int) -> String { get }
}

public protocol SiblingLabelLooksLikeSynthetic: AnyObject {
    subscript(index0 key: Int) -> String { get }
}

public func readSiblingLabelKeyword(_ x: any SiblingLabelKeyword, _ index: Int) -> String {
    return x[default: index]
}

public func readSiblingLabelLooksLikeSynthetic(_ x: any SiblingLabelLooksLikeSynthetic, _ key: Int) -> String {
    return x[index0: key]
}

// MARK: - Free-function keyword-label edge case
//
// Not a sibling-group test: this exercises the @_cdecl wrapper's *method* call site
// (CdeclParamMapper.BuildSwiftCallArgLabel), which previously stripped the C#-keyword
// prefix without backtick-escaping. A free function with a `default:` external label
// would otherwise emit `someType.foo(default: ...)` in the generated Swift wrapper —
// a syntax error.

public func freeFunctionWithKeywordLabel(default x: Int) -> Int {
    return x * 3
}

// MARK: - r6 phantom-owner: mixed-generic protocol must not own a sibling group
//
// A "mixed-generic protocol" (per EveryProtocolEmitter.IsMixedGenericProtocol) is
// one that mixes a method-level generic with a non-generic instance member. Its
// EveryProtocol conformance emits fatalError() stubs for ALL properties and
// subscripts — the type-projection pipeline can't render the non-generic members
// correctly while method-level generics are in scope.
//
// Pre-r6 bug: if a mixed-generic protocol shared a property name+type with a
// plain sibling, ComputePropertyEmissionPlans picked the mixed-generic as owner
// (lex tie-break under equal accessor sets — "PhantomOwnerMixedGeneric" sorts
// before "PhantomOwnerRegular"). The owner's body was a fatalError stub; the
// plain sibling emitted an empty extension and Swift's cross-extension witness
// resolution stitched its requirement into the stub. Dispatch through the
// plain sibling existential routed into the stub at runtime — fatalError.
//
// r6 fix: ModuleHandler's `IsEmittable` predicate filters mixed-generic protocols
// out of the sibling-plan input, so the plain sibling owns its body standalone.
// PhantomOwnerMixedGeneric still emits its own (fatalError-stub) extension, but
// it's no longer "ownership-eligible" — its stub cannot poison the sibling group.
//
// Both protocols use `{ get set }` (instead of `{ get }`) so they tie in the
// OrderByDescending(HasSetter) sort and the lex tie-break — which makes mixed-
// generic the pre-r6 owner — is the deciding factor. With only `{ get }` on both,
// some other parser path elsewhere chose Regular regardless, so the bug shape
// wasn't exercised. The setter form forces ownership selection to run through
// the IsEmittable filter the test is actually meant to verify.

public protocol PhantomOwnerMixedGeneric: AnyObject {
    func processGenericPhantom<T>(_ value: T) -> T
    var phantomName: String { get set }
}

public protocol PhantomOwnerRegular: AnyObject {
    var phantomName: String { get set }
}

public func readPhantomNameViaRegular(_ x: any PhantomOwnerRegular) -> String {
    return x.phantomName
}

public func writePhantomNameViaRegular(_ x: any PhantomOwnerRegular, _ v: String) {
    x.phantomName = v
}

// MARK: - Mixed-generic under-detection: method has both method-level AND Self generic
//
// `IsMixedGenericProtocol` originally classified protocols via `HasOnlyMethodLevelGenerics`,
// which returns false when a method carries BOTH a method-level generic (τ_1_*) AND a Self
// type param (τ_0_*) — Self short-circuits the check. A protocol whose only generic method
// has that combined shape was therefore NOT recognized as mixed-generic, slipped past the
// `IsEmittable` filter, and could win ownership of a sibling group via the lex tie-break.
//
// Both protocol names begin with `Combined…` and the mixed variant sorts before the regular
// sibling (`Mixed` < `Regular`). Both declare `{ get set }` so the OrderByDescending(HasSetter)
// sort ties and the lex tie-break is the deciding factor. Pre-fix: CombinedMixedSelfGeneric
// owns the body, its vtable is the only one consulted; a C# proxy implementing only
// CombinedRegularSibling leaves the mixed vtable nil → force-unwrap SIGSEGV when Swift
// dispatches through the regular existential and CEWR routes into the mixed-owned body.
//
// Post-fix: `HasMethodLevelGenericInSignature` ignores Self and catches the τ_1_* leg, so
// CombinedMixedSelfGeneric is classified mixed-generic, excluded from `planInputProtocols`,
// and CombinedRegularSibling owns its body standalone.

public protocol CombinedMixedSelfGeneric: AnyObject {
    func combineWithSelf<T>(_ value: T, withBase: Self) -> T
    var combinedName: String { get set }
}

public protocol CombinedRegularSibling: AnyObject {
    var combinedName: String { get set }
}

public func readCombinedNameViaRegular(_ x: any CombinedRegularSibling) -> String {
    return x.combinedName
}

public func writeCombinedNameViaRegular(_ x: any CombinedRegularSibling, _ v: String) {
    x.combinedName = v
}
