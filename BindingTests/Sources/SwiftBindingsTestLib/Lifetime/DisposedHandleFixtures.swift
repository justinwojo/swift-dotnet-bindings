// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A disposed wrapper reaching Swift
//
// A disposed C# class wrapper's handle reads zero. Before the fix, equality and
// hashing handed that zero to Swift as an object (a null dereference inside the
// Equatable/Hashable witness), an Objective-C-rooted receiver or argument crossed
// as a null `self` or a null non-optional object, and a disposed optional argument
// silently arrived in Swift as `nil`. Each shape below is reached from C# with a
// disposed wrapper: equality answers by reference identity, hashing by identity,
// and every call that would hand Swift the object refuses it with
// ObjectDisposedException before Swift runs.

/// A pure-Swift Hashable class: C# `Equals`/`GetHashCode` come from its witnesses
/// and `==` from the declared operator.
public final class DisposalProbe: Hashable {
    public let tag: Int32
    public init(tag: Int32) { self.tag = tag }

    public static func == (lhs: DisposalProbe, rhs: DisposalProbe) -> Bool { lhs.tag == rhs.tag }
    public func hash(into hasher: inout Hasher) { hasher.combine(tag) }

    public func readTag() -> Int32 { tag }
    public func tagOf(_ other: DisposalProbe) -> Int32 { other.tag }
    public func tagOrMinusOne(_ other: DisposalProbe?) -> Int32 { other?.tag ?? -1 }
}

/// The Objective-C-rooted twin: its wrapper's handle is `NSObject.Handle`.
public final class DisposalObjCProbe: NSObject {
    public let tag: Int32
    public init(tag: Int32) {
        self.tag = tag
        super.init()
    }

    public static func == (lhs: DisposalObjCProbe, rhs: DisposalObjCProbe) -> Bool { lhs.tag == rhs.tag }

    public func readTag() -> Int32 { tag }
    public func tagOf(_ other: DisposalObjCProbe) -> Int32 { other.tag }
    public func tagOrMinusOne(_ other: DisposalObjCProbe?) -> Int32 { other?.tag ?? -1 }
}

/// Free-function arguments, optional and not.
public func disposalProbeTag(_ probe: DisposalProbe) -> Int32 { probe.tag }
public func disposalProbeTagOrMinusOne(_ probe: DisposalProbe?) -> Int32 { probe?.tag ?? -1 }
public func disposalObjCProbeTag(_ probe: DisposalObjCProbe) -> Int32 { probe.tag }
public func disposalObjCProbeTagOrMinusOne(_ probe: DisposalObjCProbe?) -> Int32 { probe?.tag ?? -1 }
