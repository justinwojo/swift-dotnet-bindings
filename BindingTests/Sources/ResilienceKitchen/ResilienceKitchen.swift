// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// ResilienceKitchen — the durable BindingTests gate for the wrapper verify-recover loop.
//
// This is its OWN small Swift library target (NOT part of SwiftBindingsTestLib). Every type here
// interleaves a STRUCTURALLY HOSTILE member — one the emitter binds WRONG, producing a Swift
// `@_cdecl` wrapper that does not compile — with HEALTHY siblings inside the SAME type. The
// verify-recover loop must withdraw only the broken accessor group and keep every healthy sibling
// intact. The gate (build/Build.BindingTests.ResilienceKitchen.cs) generates this fixture on every
// `nuke binding-tests --compile-only` and asserts the loop did exactly that.
//
// The hostile shape is an implicitly-unwrapped-optional (IUO) stored property on a GENERIC class.
// To reach a member of a generic type from a non-generic `@_cdecl` function, the emitter synthesizes
// a private witness protocol carrying just that member plus an UNCONDITIONAL conformance
// (`extension KitchenBox: _SBW_… {}`), then dispatches through `as! any _SBW_…`. That requirement is
// emitted with the member's explicit-Optional type (`var hostileWidget: Optional<KitchenWidget>`),
// but an IUO stored property (`KitchenWidget!`) does NOT satisfy an explicit `Optional<…>` protocol
// requirement — swiftc reports "type 'KitchenBox<Element>' does not conform … candidate has
// non-matching type 'KitchenWidget?'". So the synthesized wrapper fails to compile and the loop
// withdraws the accessor group. A regular-Optional sibling (`KitchenWidget?`) witnesses cleanly and
// survives. This is a LOOP-CONTAINED family (its root cause — re-sugaring the IUO in the synthesized
// requirement, or bypassing the witness protocol for stored properties — is deliberately NOT fixed,
// so a natural compile→attribute→withdraw shape stays exercised here).
//
// The hostile members are gated behind `#if RESILIENCE_HOSTILE` so the gate can build a second,
// hostile-free CONTROL slice and prove the healthy siblings keep IDENTICAL C# names and collision
// suffixes whether or not the hostile members are present (the withdrawal must not perturb them).

import Foundation

/// A plain reference type used both as the IUO payload and as the healthy-Optional sibling type.
public class KitchenWidget {
    public let label: String
    public init(label: String) { self.label = label }
    public func describe() -> String { return label }
}

/// Generic reference type: a hostile IUO stored property beside a healthy regular-Optional sibling
/// of the same type, a healthy scalar sibling, and a healthy method — all in one type.
public class KitchenBox<Element> {
    public var tag: Int
    public var healthyWidget: KitchenWidget?
    public init(tag: Int) { self.tag = tag }
    public func describeTag() -> Int { return tag }

    #if RESILIENCE_HOSTILE
    // HOSTILE (loop-contained): IUO stored property on a generic class — the synthesized
    // witness-protocol conformance does not compile, so the loop withdraws this accessor group.
    public var hostileWidget: KitchenWidget!
    #endif
}

/// A second generic type with the same interleave, proving the family is not a one-off.
public class KitchenPair<First, Second> {
    public var first: KitchenWidget?
    public var count: Int
    public init(count: Int) { self.count = count }
    public func peekCount() -> Int { return count }

    #if RESILIENCE_HOSTILE
    // HOSTILE (loop-contained): same IUO-on-generic-class shape.
    public var hostileSecond: KitchenWidget!
    #endif
}

/// Positive control: a fully-bindable non-generic type with no hostile members. Its presence in the
/// emitted C# proves the fixture yields a genuine PARTIAL binding, not an empty shell.
public class KitchenPlain {
    public var value: Int
    public init(value: Int) { self.value = value }
    public func doubled() -> Int { return value * 2 }
}
