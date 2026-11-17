// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol requirements named like the generated carrier's own members
//
// A C# class implementing a Swift protocol reaches Swift through a generated
// carrier class (`EveryProtocol`, or `EveryObjCProtocol` for an NSObjectProtocol),
// which conforms to the protocol in an extension. Swift's unqualified lookup inside
// that extension finds the carrier's members first, so any requirement named like
// one of them broke the whole wrapper module:
//
//   * `handle` and `onDeinit` were stored properties of the carrier itself — a
//     requirement with either name was an invalid redeclaration;
//   * `onDeinitCtx` the same, for the deinit context;
//   * `_<protocol>_vtable` is the name of the protocol's file-scope vtable global —
//     a requirement with that name shadowed it inside every witness body
//     ("value of type 'Int32' has no member 'func_…'").
//
// The fix gives the carrier no member a requirement can name: it stores only the
// deinit callback and context, under names lengthened past every requirement name
// of the conformed protocols, and lengthens a vtable global the same way.
// `_sbwDeinitCallback` / `_sbwDeinitContext` below are those reserved names, so the
// lengthening itself is exercised.
//
// Same-named members share a kind across the two protocols (property `handle`,
// method `onDeinitCtx(_:)`): the module-wide sibling grouping refuses a
// property/method pair with one name, which would skip a protocol for an unrelated
// reason.

public protocol CarrierMemberNamed: AnyObject {
    var handle: Int32 { get }
    var onDeinit: Int32 { get set }
    func onDeinitCtx(_ value: Int32) -> Int32
    var _carrierMemberNamed_vtable: Int32 { get }
    var _sbwDeinitCallback: Int32 { get }
    var _sbwDeinitContext: Int32 { get }
}

/// Reads every requirement through the existential, writing `onDeinit` once, and
/// folds the results into one number so a single wrong dispatch shows.
public func readCarrierMemberNamed(_ value: CarrierMemberNamed) -> Int32 {
    value.onDeinit = value.onDeinit + 1
    return value.handle * 100_000
        + value.onDeinit * 10_000
        + value.onDeinitCtx(3) * 1_000
        + value._carrierMemberNamed_vtable * 100
        + value._sbwDeinitCallback * 10
        + value._sbwDeinitContext
}

@objc public protocol ObjCCarrierMemberNamed: NSObjectProtocol {
    var handle: Int32 { get }
    func onDeinitCtx(_ value: Int32) -> Int32
}

public func readObjCCarrierMemberNamed(_ value: ObjCCarrierMemberNamed) -> Int32 {
    value.handle * 100 + value.onDeinitCtx(4)
}
