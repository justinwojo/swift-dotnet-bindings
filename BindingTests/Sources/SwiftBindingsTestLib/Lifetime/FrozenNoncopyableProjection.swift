// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - `@frozen` + `~Copyable`: the two projections, side by side
//
// A `~Copyable` struct reaches C# by one of two routes, and which one it takes is decided by
// whether it carries a reference-bearing stored field — NOT by whether it is `~Copyable`:
//
//   * no reference-bearing field  → a plain by-value C# struct with NO payload. There is then no
//     handle to mark consumed, `Dispose()` is a no-op even though Swift runs a `deinit`, and plain
//     C# assignment silently duplicates a value Swift permits exactly one owner of. All three are
//     unsound and all three COMPILE, so the type is refused at emission rather than projected.
//   * a reference-bearing field  → a C# class carrying a real payload handle, which is what the
//     consumed-ownership machinery (`MarkConsumed`, the "already consumed" guard) is written
//     against. That flavor is fully supported and must keep emitting.
//
// The pair below pins that boundary end-to-end. `FrozenPlainToken` is the refused flavor and
// `FrozenLabeledToken` the admitted one; they are otherwise the same shape, so a change that
// refuses too much or too little moves exactly one of them.

// MARK: - Refused flavor: frozen, non-copyable, no reference-bearing field

/// A `@frozen ~Copyable` struct whose only stored property is trivial, so the whole value is
/// register-sized POD. Its C# projection would be a bare struct with no payload — see the header
/// above — so the generator refuses the type outright and prunes everything that mentions it.
///
/// Nothing here is expected to reach C#. The fixture exists so the refusal, and the dependent
/// pruning it drives, are visible in the generated corpus rather than only in a unit test.
@frozen
public struct FrozenPlainToken: ~Copyable {
    public let serial: Int32

    public init(serial: Int32) {
        self.serial = serial
    }

    /// A borrowing read. Not expected to bind — it hangs off a refused type.
    public borrowing func peek() -> Int32 {
        return serial
    }

    deinit {
        // A `deinit` is the point: C# would emit a no-op `Dispose()` for a payload-free struct, so
        // this cleanup would never run.
    }
}

/// Free function over the refused flavor — the free-function half of the dependent-pruning check.
public func borrowFrozenPlainToken(_ token: borrowing FrozenPlainToken) -> Int32 {
    return token.peek()
}

/// Consuming free function over the refused flavor. There is no payload whose lifetime a
/// `consuming` parameter could mark, which is the third of the three unsoundnesses.
public func consumeFrozenPlainToken(_ token: consuming FrozenPlainToken) -> Int32 {
    return token.peek()
}

/// A member on an UNRELATED, fully supported type that mentions the refused one. It is the
/// member-half of the dependent-pruning check: this method must be pruned at the member gate while
/// its declaring class keeps binding normally.
public final class FrozenTokenDesk {
    public init() {}

    /// Pruned — its signature references a refused type.
    public func inspect(_ token: borrowing FrozenPlainToken) -> Int32 {
        return token.peek()
    }

    /// The positive control on the same class: an ordinary member that must survive, so a pruned
    /// `inspect` reads as targeted pruning rather than the class collapsing.
    public func deskId() -> Int32 {
        return 1
    }
}

// MARK: - Admitted flavor: frozen, non-copyable, WITH a reference-bearing field

/// The same shape as `FrozenPlainToken` plus one reference-bearing stored property, which is what
/// moves it onto the payload-carrying class projection. Everything the consumed-ownership path
/// needs exists here, so this type binds in full.
///
/// It feeds the shared allocation counters (see `Lifetime/OwnershipTests.swift`) so a C# test can
/// assert the `deinit` runs EXACTLY once when the value is handed to a `consuming` function — the
/// same deterministic live-count assertion the non-frozen `TrackedResource` uses.
@frozen
public struct FrozenLabeledToken: ~Copyable {
    public let label: String

    public init(label: String) {
        self.label = label
        recordTrackedAllocation()
    }

    /// Borrowing read — does not consume.
    public borrowing func peek() -> String {
        return label
    }

    /// Consuming SELF: Swift takes ownership, runs `deinit` once inside the call, and the C# handle
    /// must be marked consumed so a later `Dispose()` is a no-op rather than a second destroy.
    public consuming func redeem() -> String {
        return label
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Creates a `FrozenLabeledToken` (bumps the live-object counter).
public func createFrozenLabeledToken(label: String) -> FrozenLabeledToken {
    return FrozenLabeledToken(label: label)
}

/// Borrows the admitted flavor — the borrow half of the runtime check.
public func borrowFrozenLabeledToken(_ token: borrowing FrozenLabeledToken) -> String {
    return token.peek()
}

/// Takes ownership of the admitted flavor. Swift consumes — and so `deinit`s — the value exactly
/// once inside this call; the caller's handle is left invalid.
public func consumeFrozenLabeledToken(_ token: consuming FrozenLabeledToken) -> String {
    return token.peek()
}

/// A member on an unrelated type that mentions the admitted flavor — the mirror of
/// `FrozenTokenDesk.inspect`. This one must NOT be pruned.
public final class FrozenLabeledTokenDesk {
    public init() {}

    public func inspect(_ token: borrowing FrozenLabeledToken) -> String {
        return token.peek()
    }
}

// MARK: - The one slot a `~Copyable` value can legally occupy generically
//
// Swift refuses a non-copyable value in every erased or container slot: an `Array` element, a
// tuple element, an `any P` existential and an ordinary `<T>` generic parameter all carry an
// implicit `T: Copyable`. The single exception is a generic parameter that opts out with
// `<T: ~Copyable>`. That is therefore the ONLY public-API shape through which a `~Copyable`
// value could reach a marshalling path that treats it as an opaque payload, so it is the shape
// this fixture pins — for both projections at once, since the constraint is on the callee.
public func inspectNoncopyableGenerically<T: ~Copyable>(_ value: borrowing T) -> Int32 {
    return 7
}

/// The consuming half of that same slot. C# must MOVE the value into the argument buffer either
/// way — a copy is the non-copyable trap — but who destroys the buffer afterwards differs: a
/// `consuming` parameter is passed `@in`, so the callee owns the buffer and runs the `deinit`, and
/// a caller-side destroy on top of that is the second one over the same storage. Exactly one
/// `deinit` must run, and the caller's handle must be left marked consumed either way.
public func discardNoncopyableGenerically<T: ~Copyable>(_ value: consuming T) {
}

// MARK: - `init` taking a `~Copyable` parameter
//
// A constructor is the one member kind that used to be refused the @_cdecl wrapper for a
// `~Copyable` parameter, which did not make it safe — it routed the constructor to the native
// thunk / direct P/Invoke instead, where the callee-consumes hand-over copies the argument through
// its value witness. Both flavors are pinned here because the routing decision is made on the
// PARAMETER, so each projection has to be shown taking the pointer-passing wrapper path.

/// Takes the frozen, reference-bearing `~Copyable` flavor by `borrowing` — the caller keeps
/// ownership and must still be able to use and dispose the token afterwards.
public final class FrozenTokenReceipt {
    public let label: String
    public let serial: Int32

    public init(token: borrowing FrozenLabeledToken, serial: Int32) {
        self.label = token.peek()
        self.serial = serial
    }
}

/// Takes the frozen flavor by `consuming` — Swift runs the token's `deinit` inside this
/// initializer, exactly once, and the caller's handle must be left marked consumed.
public final class FrozenTokenVault {
    public let label: String

    public init(consuming token: consuming FrozenLabeledToken) {
        self.label = token.peek()
    }
}

// MARK: - Refused shape: a consumed `~Copyable` on a route that cannot move it
//
// The move that empties the caller's buffer — and the `MarkConsumed` that disarms the caller's
// destroy — live in the `@_cdecl` wrapper. Whether a member gets one is decided by its WHOLE
// signature, so a parameter that has nothing to do with ownership can send the call to Swift's own
// symbol instead, where the token is destroyed by the callee's `deinit` and again by its value
// witness on return. Both compilers accept that emission and it only misbehaves at run time, so
// the member is refused at generation rather than emitted borrowed.

/// The nested frozen struct beside the token is what declines the wrapper; the `consuming` token
/// is then unreachable by any move, so this initializer must be refused.
public struct NestedFrozenTokenHost {
    public let label: String
    public let value: Int32

    public init(inner: NestedOuter.Inner, token: consuming FrozenLabeledToken) {
        self.value = inner.value
        self.label = token.peek()
    }

    /// Positive control on the same type: the same nested parameter beside the same token type, but
    /// `borrowing`, which asks for no hand-over at all and therefore keeps binding on the same
    /// route. A refusal that swallowed this one too would be over-broad. The extra parameter keeps
    /// the two initializers apart once projected — they would otherwise collide on identical C#
    /// parameter types and be deduplicated rather than judged on ownership.
    public init(inner: NestedOuter.Inner, borrowedToken: borrowing FrozenLabeledToken, serial: Int32) {
        self.value = inner.value + serial
        self.label = borrowedToken.peek()
    }
}

/// The non-frozen (opaque-payload) flavor's half of the same pair. `TrackedResource` lives in
/// `Lifetime/OwnershipTests.swift` and feeds the same allocation counters.
public final class TrackedResourceReceipt {
    public let peeked: Int32

    public init(resource: borrowing TrackedResource, bump: Int32) {
        self.peeked = resource.peek() + bump
    }
}

public final class TrackedResourceVault {
    public let peeked: Int32

    public init(consuming resource: consuming TrackedResource) {
        self.peeked = resource.peek()
    }
}

// MARK: - Refused shapes: a `~Copyable` reached through a generic slot
//
// `Optional<Wrapped>` is declared `Wrapped: ~Copyable` in the standard library, so `Token?` is
// itself non-copyable — but it is spelled by `Swift.Optional`, whose own record is copyable, so
// every marshalling arm downstream would reach for `Optional`'s value witness and copy. That copy
// is `__swift_cannot_copy_noncopyable_type`: a trap at run time, not a compile error, which is why
// these must be refused at emission rather than left to the verify-recover loop. They are fixtures
// for the REFUSAL — each must appear in the generated output under a skip marker, never bound.

/// Refused: `~Copyable` behind an Optional, frozen flavor.
public func inspectOptionalFrozenToken(_ token: borrowing FrozenLabeledToken?) -> Int32 {
    return token == nil ? 0 : 1
}

/// Refused: `~Copyable` behind an Optional, non-frozen flavor.
public func inspectOptionalTrackedResource(_ resource: borrowing TrackedResource?) -> Int32 {
    return resource == nil ? 0 : 1
}

/// Refused type: a `~Copyable` enum. No enum projection can express move-only semantics — a
/// payload-free one becomes a plain copyable C# enum, and an associated-value one has its cases
/// built and read through value-witness copies. The whole type is withdrawn, and the free function
/// below it is withdrawn as a dependent skip.
public enum NoncopyableToken: ~Copyable {
    case idle
    case armed(Int32)
}

public func describeNoncopyableToken(_ token: borrowing NoncopyableToken) -> Int32 {
    switch token {
    case .idle: return 0
    case .armed(let n): return n
    }
}
