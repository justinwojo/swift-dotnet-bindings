// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-carrier INHERITED-requirement suppression (refined protocol)
//
// This is the inheritance sibling of CrossCarrierSignatureCollision.swift. There
// the collision was between two UNRELATED protocols sharing one signature; here it
// is a child protocol that REFINES a parent whose umbrella conformance lands on a
// DIFFERENT concrete carrier class.
//
// A protocol's umbrella conformance is emitted on a carrier class chosen per
// protocol: one that transitively requires NSObjectProtocol routes to the
// NSObject-rooted `EveryObjCProtocol`; every other protocol routes to plain
// `EveryProtocol`. `CarrierValidationParent` requires no NSObjectProtocol, so it
// routes to `EveryProtocol`. `CarrierValidationChild` refines the parent AND
// NSObjectProtocol, so it routes to `EveryObjCProtocol`.
//
// Swift then requires the child's carrier (`EveryObjCProtocol`) to also satisfy the
// inherited parent requirement — but the parent's witness body was emitted on
// `EveryProtocol`, and Swift's cross-extension witness resolution does not bridge
// two distinct concrete carrier types. Pre-fix the emitter still wrote
// `extension EveryObjCProtocol: CarrierValidationChild`, so the wrapper module
// failed to compile with:
//
//   type 'EveryObjCProtocol' does not conform to protocol 'CarrierValidationParent'
//
// The fix detects the cross-carrier inherited requirement and suppresses the
// child's umbrella conformance fail-closed, while leaving the parent's own
// `EveryProtocol` conformance intact. `nuke binding-tests --compile-only` is the
// structural gate (the wrapper module must type-check); the runtime test below
// additionally proves the parent's plain-carrier conformance still dispatches into
// a managed implementation — i.e. the suppression is surgical, not a blanket drop.

/// Plain Swift protocol — no NSObjectProtocol, so it routes through the
/// `EveryProtocol` carrier. Reverse-dispatched into by a C# conformer.
public protocol CarrierValidationParent: AnyObject {
    func validationLabel() -> String
}

/// Refines `CarrierValidationParent` AND `NSObjectProtocol`, so it routes through
/// the NSObject-rooted `EveryObjCProtocol` carrier — a different concrete class
/// than the parent's. Its mere presence forces the emitter to reconcile the two
/// carriers; pre-fix that produced an unsatisfiable `extension EveryObjCProtocol:
/// CarrierValidationChild` and broke wrapper compilation. The fix suppresses this
/// child's umbrella conformance.
public protocol CarrierValidationChild: CarrierValidationParent, NSObjectProtocol {
    var childFlag: Bool { get }
}

/// Free function that reverse-dispatches the PARENT requirement into whatever
/// conformer (C# or Swift) is passed. Reaching this at runtime already proves the
/// wrapper compiled; the returned value proves the parent's `EveryProtocol` witness
/// dispatched into the managed implementation rather than being suppressed alongside
/// the child.
public func readValidationLabel(_ provider: CarrierValidationParent) -> String {
    return provider.validationLabel()
}

// MARK: - Cross-MODULE carrier-split variant (consuming half)
//
// Same carrier split as above, but the parent (`CrossCarrierCrossModuleParent`)
// lives in the dependency module SwiftBindingsTestLibDependency, so its umbrella
// conformance is emitted there on plain `EveryProtocol`. The child below refines
// that cross-module parent AND NSObjectProtocol, so it routes to this module's
// NSObject-rooted `EveryObjCProtocol` carrier. The suppression gate must resolve
// the parent's carrier ACROSS the module boundary; if it only inspects same-module
// protocols it silently misses the split and emits an unsatisfiable
// `extension EveryObjCProtocol: CrossCarrierCrossModuleChild`, breaking wrapper
// compilation with:
//
//   type 'EveryObjCProtocol' does not conform to protocol 'CrossCarrierCrossModuleParent'
//
// `nuke binding-tests --compile-only` is the structural gate; the runtime function
// below proves the cross-module parent's `EveryProtocol` conformance still
// dispatches into a managed implementation after the child is suppressed.

/// Refines a DEPENDENCY-module parent AND NSObjectProtocol, so it routes to this
/// module's `EveryObjCProtocol` carrier while the parent's witness lives on the
/// dependency module's `EveryProtocol` — a cross-module carrier split.
public protocol CrossCarrierCrossModuleChild: CrossCarrierCrossModuleParent, NSObjectProtocol {
    var crossChildMark: Bool { get }
}

/// Reverse-dispatches the cross-module PARENT requirement into whatever conformer
/// is passed. Reaching this at runtime proves the wrapper compiled; the returned
/// value proves the parent's cross-module `EveryProtocol` witness dispatched into
/// the managed implementation rather than being suppressed with the child.
public func readCrossModuleValidationLabel(_ provider: CrossCarrierCrossModuleParent) -> String {
    return provider.crossCarrierLabel()
}
