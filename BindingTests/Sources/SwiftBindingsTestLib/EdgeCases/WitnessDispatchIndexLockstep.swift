// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Forward witness-dispatch index lockstep (regression R5-1a)
//
// The forward witness-dispatch path numbers a protocol's methods with a running
// counter and bakes that index into each `SBW_<proto>_method_<name>_<idx>` symbol.
// The Swift @_cdecl producer and BOTH C# consumer walks (the P/Invoke declaration
// and the call site) must advance the counter in true lockstep, or a later method's
// symbol index differs between the Swift export and the C# `EntryPoint` →
// `EntryPointNotFoundException` at runtime.
//
// The counter is gated on a per-method dedup key. The producer keys on the RAW Swift
// type spec; if a consumer instead keys on the PROJECTED C# type, two overloads whose
// DISTINCT Swift parameter types both fall back to the same C# projection collapse to
// ONE index on the consumer but stay TWO on the producer — shifting every later method.
//
// This protocol reproduces that exact shape: two `consume` overloads whose parameters
// are distinct parameterized-PAT existentials (each degrades to the same C# projection,
// so the projected key collapses the pair) declared BEFORE the dispatchable required
// `tag(_:Int32) -> Int32`. `tag` must occupy witness index 2 on BOTH sides. The runtime
// test obtains a Swift-vended `any WitnessIndexProto` and calls `tag` through the proxy;
// a green run proves the SBW index did not shift (pre-fix it resolved `SBW_..._tag_1`
// against a producer that exported `SBW_..._tag_2`).

/// First parameterized PAT used only to make `consume`'s parameter generator-unresolvable.
public protocol WitnessIndexPayloadA {
    associatedtype Element
}

/// Second, DISTINCT parameterized PAT — a different Swift type that projects to the same
/// C# fallback as `WitnessIndexPayloadA`, so the two `consume` overloads share a projected
/// C# key while remaining two distinct raw-Swift witness-table requirements.
public protocol WitnessIndexPayloadB {
    associatedtype Element
}

public protocol WitnessIndexProto {
    /// Overload 1 — distinct Swift spec, generator-unresolvable param. Witness index 0.
    func consume(_ payload: any WitnessIndexPayloadA)

    /// Overload 2 — distinct Swift spec, same C# projection as overload 1. Witness index 1.
    func consume(_ payload: any WitnessIndexPayloadB)

    /// Dispatchable required method declared AFTER the unresolvable overloads. Must occupy
    /// witness index 2 on both the Swift producer and the C# consumer.
    func tag(_ value: Int32) -> Int32
}

/// Minimal Swift conformer vended to C# as `any WitnessIndexProto`.
public class WitnessIndexConformer: WitnessIndexProto {
    public init() {}

    public func consume(_ payload: any WitnessIndexPayloadA) {}
    public func consume(_ payload: any WitnessIndexPayloadB) {}

    public func tag(_ value: Int32) -> Int32 {
        return value &+ 1
    }
}

/// Factory returning the conformer as a protocol existential so the C# side dispatches
/// `tag` through the forward witness path (`SBW_WitnessIndexProto_method_tag_2`).
public func makeWitnessIndexConformer() -> any WitnessIndexProto {
    return WitnessIndexConformer()
}
