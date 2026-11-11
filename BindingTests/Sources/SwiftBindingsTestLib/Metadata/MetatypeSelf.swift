// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Class metatype in the Swift self register
//
// A static member, `class func` or allocating initializer of a class receives its
// `@thick Self.Type` in the Swift self register (x20 on arm64, r13 on x86_64). A binding that
// calls one directly through the Swift calling convention must load the class metadata there;
// otherwise the callee reads whatever the register last held as its metatype. A struct or enum
// static has a thin metatype and no self register at all, so only class-shaped parents are here.
//
// Every probe reports the metatype it received, so a wrong or stale register fails an assertion
// rather than passing by luck. The parents are not `final`, so the optimizer cannot replace
// `self` with the declared class. Probes on public parents return `(any Error)?`, an
// address-only result that keeps them on the direct Swift-convention route instead of a
// C-convention wrapper (which would supply the metatype itself and hide a missing one).

/// Carries the metatype a probe observed in its self register, rendered as
/// `metatype=<Module.Type>` so a test can match it exactly.
public struct MetatypeSelfReport: Error, LocalizedError, CustomStringConvertible {
    public let observed: String

    public init(observed: String) {
        self.observed = observed
    }

    public var description: String { "metatype=<\(observed)>" }
    public var errorDescription: String? { description }
}

/// Static and class members of a public, non-final class.
open class StaticSelfErrorProbe {
    public init() {}

    /// `nil` when the self register held this class's metatype, otherwise a report naming what
    /// it held.
    public static func check() -> (any Error)? {
        self == StaticSelfErrorProbe.self ? nil : MetatypeSelfReport(observed: String(reflecting: self))
    }

    /// Overridden below. Reports which body ran and the metatype it received. Only its dispatch
    /// thunk is exported, and the thunk finds the body through the vtable of the metatype in self.
    open class func report() -> (any Error)? {
        MetatypeSelfReport(observed: "base:" + String(reflecting: self))
    }

    /// Overridden below. Its dynamic counterpart: the instance in self picks the body.
    open func instanceReport() -> (any Error)? {
        MetatypeSelfReport(observed: "base-instance:" + String(reflecting: type(of: self)))
    }

    /// Overridden below. A `class` property read through the metatype.
    open class var kindName: String { "base:" + String(reflecting: self) }
}

/// Subclass overriding each overridable member with the same signature, so Swift gives none of
/// the overrides a vtable entry or dispatch thunk of its own: each is reached through the base
/// class's thunk with the subclass metatype or instance in self.
open class StaticSelfErrorProbeDerived: StaticSelfErrorProbe {
    public override init() {
        super.init()
    }

    open override class func report() -> (any Error)? {
        MetatypeSelfReport(observed: "derived:" + String(reflecting: self))
    }

    open override func instanceReport() -> (any Error)? {
        MetatypeSelfReport(observed: "derived-instance:" + String(reflecting: type(of: self)))
    }

    open override class var kindName: String { "derived:" + String(reflecting: self) }
}

/// An actor's static member takes the actor's metatype in the self register, exactly as a
/// class's does.
public actor MetatypeSelfActor {
    public init() {}

    public static func report() -> (any Error)? {
        MetatypeSelfReport(observed: String(reflecting: self))
    }
}

/// A nonisolated static on a class isolated to a custom global actor. Isolation does not change
/// the convention: it is still a class static with a thick metatype.
@BindingsTestGlobalActor
public class MetatypeSelfGlobalActorBound {
    nonisolated public init() {}

    nonisolated public static func report() -> (any Error)? {
        MetatypeSelfReport(observed: String(reflecting: self))
    }
}

/// A `@usableFromInline internal` parent: no Swift wrapper can name it, so its static members are
/// reached directly through the Swift calling convention even with ordinary return types.
@usableFromInline
internal class MetatypeSelfInternalHost {
    public static func describeSelf() -> String { String(reflecting: self) }

    public static var selfName: String { String(reflecting: self) }
}
