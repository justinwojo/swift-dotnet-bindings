// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Subclass-closed generic parent: a generic class concretized through a declared
// `final` subclass that supplies all of the parent's type arguments, rather than
// being instantiated directly with type parameters at the call site. Mirrors a
// view-model / controller base whose lifecycle and control methods must be callable
// from C# on the concrete subclass instance. Two shapes are exercised:
//
//   1. UNCONSTRAINED parameters — the open generic already emits working methods via
//      runtime metadata, so this is the control: the concrete subclass must inherit
//      and round-trip those methods.
//   2. MIXED unconstrained + protocol-with-associated-type constrained — the PAT
//      constraint makes the parent's methods fail C#-generic emission, and the
//      existing conformer-enumeration specialization is all-or-nothing (the
//      unconstrained parameter has no enumerable conformer set), so it cannot fire.
//      The `final` subclass closing BOTH parameters with concrete types supplies the
//      instantiation directly.

// MARK: - Shape 1: unconstrained generic parent, subclass-closed

public final class ScanReadout {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

public final class ScanBanner {
    public let kind: Int32
    public init(kind: Int32) { self.kind = kind }
}

/// Generic base with UNCONSTRAINED parameters. The control methods never reference
/// the generic parameters; they mutate private phase state witnessed by `currentPhase`.
public class LifecycleKernel<Readout, Banner> {
    private var phase: Int32 = 0
    public init() {}

    public func pause() { phase = 1 }
    public func resume() { phase = 2 }
    public func restart() { phase = 0 }
    public func dismissBanner() { phase = phase &+ 10 }

    public func currentPhase() -> Int32 { return phase }
}

/// Concrete subclass closing both parameters with concrete types.
public final class ConcreteLifecycle: LifecycleKernel<ScanReadout, ScanBanner> {
    public override init() { super.init() }
}

public func makeConcreteLifecycle() -> ConcreteLifecycle {
    return ConcreteLifecycle()
}

// MARK: - Shape 2: mixed unconstrained + PAT-constrained generic parent, subclass-closed

/// Protocol with an associated type — a C# generic cannot satisfy this constraint,
/// so a parent generic parameter bound to it drops the parent's methods.
public protocol StateMachine {
    associatedtype Snapshot
    func snapshot() -> Snapshot
}

public struct ReticleState: StateMachine {
    public let level: Int32
    public init(level: Int32) { self.level = level }
    public func snapshot() -> Int32 { return level }
}

/// Generic base with one UNCONSTRAINED parameter (`Readout`) and one parameter
/// constrained to a PAT (`Gate: StateMachine`). The control methods never reference
/// either generic parameter.
public class GatedKernel<Readout, Gate: StateMachine> {
    private var ticks: Int32 = 0
    public init() {}

    public func advance() { ticks = ticks &+ 1 }
    public func reset() { ticks = 0 }
    public func tickCount() -> Int32 { return ticks }
}

/// Concrete subclass closing BOTH parameters with concrete types.
public final class ConcreteGated: GatedKernel<ScanReadout, ReticleState> {
    public override init() { super.init() }
}

public func makeConcreteGated() -> ConcreteGated {
    return ConcreteGated()
}
