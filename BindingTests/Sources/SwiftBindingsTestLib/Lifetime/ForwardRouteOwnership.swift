// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Forward-route ownership probes
//
// A class argument and a throwing call shaped for the native thunk route: free functions over a
// plain class, with no closure, generic or by-reference parameter. The runtime tests assert the
// route each one takes, then count retains, releases and deinits across it.

private let forwardRouteLock = NSLock()
private nonisolated(unsafe) var forwardRouteTokenDeinitCount: Int32 = 0

/// A class whose deinit is counted, passed by reference to the forward-route probes.
public final class ForwardRouteToken {
    public let value: Int64

    public init(value: Int64) { self.value = value }

    deinit {
        forwardRouteLock.lock()
        forwardRouteTokenDeinitCount += 1
        forwardRouteLock.unlock()
    }
}

public func forwardRouteTokenDeinits() -> Int32 {
    forwardRouteLock.lock()
    defer { forwardRouteLock.unlock() }
    return forwardRouteTokenDeinitCount
}

public enum ForwardRouteError: Error {
    case negative
}

/// Reads the token it borrows.
public func forwardRouteRead(_ token: ForwardRouteToken) -> Int64 {
    token.value
}

/// Adds `delta` to the token's value. A negative delta throws an enum error, and a delta of
/// -424242 throws a class error holding a tracked reference, so a test can count its release.
public func forwardRouteReadOrThrow(_ token: ForwardRouteToken, _ delta: Int64) throws -> Int64 {
    if delta == trackedRouteErrorSentinel { throw SyncCascadeTrackedClassError(code: 4242) }
    if delta < 0 { throw ForwardRouteError.negative }
    return token.value &+ delta
}

/// Borrows a non-trivial, address-only struct: the caller's copy must survive the call untouched.
public func forwardRouteBorrowStruct(_ value: borrowing OwnershipStructValue) -> Int32 {
    value.number
}

/// Consumes a non-trivial, address-only struct: the callee destroys what it was given, so the
/// caller's copy must hold its own reference.
public func forwardRouteConsumeStruct(_ value: consuming OwnershipStructValue) -> Int32 {
    value.number
}
