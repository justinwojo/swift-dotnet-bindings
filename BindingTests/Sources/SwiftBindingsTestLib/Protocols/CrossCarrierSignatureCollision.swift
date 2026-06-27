// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreText
import CoreGraphics

// MARK: - Cross-carrier same-signature collision
//
// A plain Swift protocol and an @objc/NSObjectProtocol protocol that declare ONE
// identical method signature route their umbrella conformances through DIFFERENT
// concrete carrier classes — the plain one through `EveryProtocol`, the
// NSObjectProtocol-only one through `EveryObjCProtocol`. These are distinct
// concrete Swift types, and Swift's cross-extension witness resolution only
// stitches a requirement into a body emitted on the SAME concrete type.
//
// The pre-fix emitter deduplicated the two requirements by signature ALONE,
// picked a single lexicographically-smaller owner protocol, emitted the witness
// body on that owner's carrier, and emitted an EMPTY extension for the sibling
// trusting cross-extension resolution to fill it in. Because the sibling's
// carrier differed, the empty extension never resolved a witness, and the
// wrapper module failed to compile with:
//
//   type 'EveryObjCProtocol' does not conform to protocol '<sibling>'
//
// The fix partitions the emission plans by carrier class as well as signature,
// so each carrier owns and emits its own satisfying witness body.
//
// `nuke binding-tests --compile-only` is the structural gate: the wrapper module
// must type-check. The String shape below additionally round-trips at runtime
// (CrossCarrierSignatureCollisionTests) to prove each carrier dispatches into
// the correct per-carrier vtable.

// MARK: Runtime-marshallable shape (String round-trip)

/// Plain Swift protocol — routed through the `EveryProtocol` carrier.
public protocol GreetingProviderPlain: AnyObject {
    func makeGreeting(for name: String) -> String
}

/// @objc / NSObjectProtocol-only protocol declaring the SAME `makeGreeting(for:)`
/// signature — routed through the `EveryObjCProtocol` carrier. Pre-fix this
/// collided with `GreetingProviderPlain` and one carrier's conformance failed to
/// type-check.
@objc public protocol GreetingProviderObjC: NSObjectProtocol {
    @objc func makeGreeting(for name: String) -> String
}

public func callGreetingPlain(_ provider: GreetingProviderPlain, name: String) -> String {
    return provider.makeGreeting(for: name)
}

public func callGreetingObjC(_ provider: GreetingProviderObjC, name: String) -> String {
    return provider.makeGreeting(for: name)
}

// MARK: Optional-CFType-return shape (compile gate — the exact failing library shape)
//
// The real-world report carried this exact pair: a plain font provider and an
// @objc-compatible font provider both declaring `fontFor(family:size:) -> CTFont?`.
// The optional CoreFoundation reference-type return was once hypothesized as the
// root cause; it is not — each carrier emits a real witness that round-trips the
// `CTFont?` through the reverse-dispatch vtable. This pair is kept as a durable
// compile gate for the optional-CFType cross-carrier shape specifically.

/// Plain protocol whose requirement returns an optional CoreText CFType.
public protocol AnimationFontProviding: AnyObject {
    func fontFor(family: String, size: CGFloat) -> CTFont?
}

/// @objc / NSObjectProtocol-only sibling declaring the SAME `fontFor(family:size:)
/// -> CTFont?` signature — the cross-carrier collision on an optional-CFType return.
@objc public protocol CompatibleFontProviding: NSObjectProtocol {
    @objc func fontFor(family: String, size: CGFloat) -> CTFont?
}
