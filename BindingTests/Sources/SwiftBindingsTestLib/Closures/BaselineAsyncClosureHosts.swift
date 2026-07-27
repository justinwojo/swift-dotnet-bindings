// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Coverage matrix for a *baseline-supported* async closure carried by a member
// that can never reach the async bridge.
//
// The async closure bridge is only wired when the CONTAINING member is promoted
// to an async `@_cdecl` wrapper: the P/Invoke passes `(context, startFunc)` and
// the Swift wrapper renders the matching adapter inside a `Task { }`. A baseline
// closure shape (`() async throws -> String`, `() async -> Int32`) passes the
// closure-support gate on its own merits, so member validation admits it and the
// unsupported-closure tombstone — which absorbs only UNsupported shapes — never
// sees it. If the containing member is synchronous, or otherwise never promoted
// to the async `@_cdecl` wrapper, the bridge's eligibility conjuncts fail and the
// parameter degrades to the `Swift.AnyType` placeholder inside a `[LibraryImport]`
// → SYSLIB1051, while the member body still emits half of the bridge state
// machine.
//
// The eligibility guard for exactly those conjuncts already exists at the handler
// layer; these hosts pin that EVERY handler path consults it, so the member is
// skipped whole (body, P/Invoke, and validator in lockstep) instead of emitting a
// half-bridged member. Rows: closure {throwing, non-throwing} × containing member
// {struct init, class init, sync method, async-but-never-cdecl method}, plus
// positive controls that must keep binding through the real bridge.

import Foundation

// MARK: - Struct initializers (definite-assignment-bearing parent)

/// Frozen-shaped struct whose init takes the throwing baseline closure. A struct
/// init is never promoted to the async `@_cdecl` method wrapper, so the bridge
/// can never materialise here.
public struct BaselineAsyncThrowingClosureStructInit {
    public let tag: Int32

    public init(tag: Int32, provider: @escaping () async throws -> String) {
        self.tag = tag
        _ = provider
    }
}

/// Same parent kind, non-throwing baseline closure — the twin bridge arm.
public struct BaselineAsyncNonThrowingClosureStructInit {
    public let tag: Int32

    public init(tag: Int32, provider: @escaping () async -> Int32) {
        self.tag = tag
        _ = provider
    }
}

// MARK: - Class initializers

/// Class init taking the throwing baseline closure. Classes have no
/// definite-assignment constraint, so this row separates the parent-kind axis
/// from the bridge-eligibility axis.
public final class BaselineAsyncThrowingClosureClassInit {
    public let tag: Int32

    public init(tag: Int32, provider: @escaping () async throws -> String) {
        self.tag = tag
        _ = provider
    }
}

/// Class init taking the non-throwing baseline closure.
public final class BaselineAsyncNonThrowingClosureClassInit {
    public let tag: Int32

    public init(tag: Int32, provider: @escaping () async -> Int32) {
        self.tag = tag
        _ = provider
    }
}

// MARK: - Synchronous and never-cdecl members

public final class BaselineAsyncClosureMemberHost {
    public init() {}

    /// Synchronous instance method carrying the throwing baseline closure.
    public func configureThrowing(provider: @escaping () async throws -> String) {
        _ = provider
    }

    /// Synchronous instance method carrying the non-throwing baseline closure.
    public func configureNonThrowing(provider: @escaping () async -> Int32) {
        _ = provider
    }

    /// Async but NOT throwing: the throwing baseline adapter needs `try await`
    /// inside the outer method's error harness, so this member is denied the
    /// async `@_cdecl` wrapper even though it is async.
    public func loadThrowingClosureFromNonThrowingAsync(
        provider: @escaping () async throws -> String
    ) async -> Int32 {
        _ = provider
        return 0
    }
}

/// Free function (module parent) carrying the throwing baseline closure on a
/// synchronous member — the parent-kind row the type handlers do not cover.
public func baselineAsyncThrowingClosureFreeFunction(
    provider: @escaping () async throws -> String
) {
    _ = provider
}

// MARK: - Sibling handler paths (operator, subscript, protocol, default-arg)

/// Operator overload carrying the throwing baseline closure. Operators are emitted
/// by their own handler, which builds a signature independently of the ordinary
/// method path.
public struct BaselineAsyncClosureOperatorHost {
    public let tag: Int32

    public init(tag: Int32) { self.tag = tag }

    public static func + (lhs: BaselineAsyncClosureOperatorHost,
                          rhs: @escaping () async throws -> String) -> BaselineAsyncClosureOperatorHost {
        _ = rhs
        return lhs
    }
}

/// Subscript whose index is a baseline async closure — the subscript accessor
/// frame builds its own signature, so it is a distinct handler path.
public struct BaselineAsyncClosureSubscriptHost {
    public let tag: Int32

    public init(tag: Int32) { self.tag = tag }

    public subscript(provider: @escaping () async -> Int32) -> Int32 {
        _ = provider
        return tag
    }
}

/// Protocol requirement carrying the baseline closure, plus a concrete conformer:
/// the protocol surface, its proxy, and the conformer's own member are three
/// separate emission paths over the same shape.
public protocol BaselineAsyncClosureRequirement {
    func acceptThrowing(provider: @escaping () async throws -> String)
}

public final class BaselineAsyncClosureConformer: BaselineAsyncClosureRequirement {
    public init() {}

    public func acceptThrowing(provider: @escaping () async throws -> String) {
        _ = provider
    }
}

/// Trailing DEFAULTED parameter after the baseline closure. When the full
/// signature is unbindable the emitter attempts truncated recovery overloads;
/// this row pins that the recovery path cannot resurrect the unbridgeable
/// closure through a different signature builder.
public final class BaselineAsyncClosureDefaultArgHost {
    public init() {}

    public func configure(provider: @escaping () async throws -> String,
                          retries: Int32 = 3) {
        _ = provider
        _ = retries
    }
}

/// Protocol requirement whose closure IS bridgeable — the containing member is
/// `async throws`, so the witness genuinely reaches the async `@_cdecl` wrapper
/// and its adapter. The conformance must therefore be KEPT.
///
/// This is the ordering trap, not a shape trap: conformance selection runs BEFORE
/// the wrapper-promotion flag is recorded, so a validator that asks the
/// emission-time question here reads a flag that is still false, concludes the
/// witness is unbridgeable, and drops the whole interface from the conformer —
/// silently losing a surface that compiles and runs. The conformer below must
/// declare the interface in the generated C#.
public protocol BaselineAsyncClosureEligibleRequirement {
    func run(provider: @escaping () async throws -> String) async throws -> String
}

public final class BaselineAsyncClosureEligibleConformer: BaselineAsyncClosureEligibleRequirement {
    public init() {}

    public func run(provider: @escaping () async throws -> String) async throws -> String {
        return try await provider()
    }
}

/// The INVERSE divergence of the case above. Here the shape is async-`@_cdecl`
/// eligible on its own terms, but the member also carries a debug default
/// parameter (`#file`), and emission installs the debug-default-parameter Swift
/// wrapper BEFORE it reaches the async promotion branch. That install claims the
/// wrapper library, so the async branch declines — a pre-emission predictor that
/// reads only the not-yet-set flag would answer "will promote", keep the
/// interface on the conformer, and then watch the witness emission skip, leaving
/// an unimplemented interface member (CS0535) or a verify-recover withdrawal of
/// the whole type. Prediction and emission must reach the SAME verdict here, so
/// the generated binding compiles either way.
///
/// The parameter is `StaticString`, not `String`: `#file` in a `StaticString`
/// position is the shape the debug-parameter detector recognizes.
public protocol BaselineAsyncClosureDebugDefaultRequirement {
    func run(provider: @escaping () async throws -> String, file: StaticString) async throws -> String
}

public final class BaselineAsyncClosureDebugDefaultConformer: BaselineAsyncClosureDebugDefaultRequirement {
    public init() {}

    public func run(
        provider: @escaping () async throws -> String,
        file: StaticString = #file
    ) async throws -> String {
        return try await provider()
    }
}

// MARK: - Positive controls (must keep binding through the real bridge)

public final class BaselineAsyncClosureBridgeControl {
    public init() {}

    /// Fully eligible: async throws member + throwing baseline closure. Must
    /// still emit the `(context, startFunc)` bridge, not a skip.
    public func runThrowing(provider: @escaping () async throws -> String) async throws -> String {
        return try await provider()
    }

    /// Fully eligible twin: async member + non-throwing baseline closure.
    public func runNonThrowing(provider: @escaping () async -> Int32) async -> Int32 {
        return await provider()
    }
}
