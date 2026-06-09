// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Closure Patterns (GRDB, Stripe)

/// Error type for throwing closure tests.
public enum ClosureError: Error {
    case invalid
    case timeout
}

/// Accepts a throwing closure that returns Int32.
/// Tests basic throwing closure emission path (ClosureEmitter.Throwing.cs).
public func callThrowingClosure(_ callback: @escaping () throws -> Int32) -> Int32 {
    do {
        return try callback()
    } catch {
        return -1
    }
}

/// Accepts a throwing closure with a parameter.
/// Tests throwing closure with arguments — should still be supported
/// (only async+throwing with params is B13-blocked).
public func callThrowingWithParam(_ callback: @escaping (Int32) throws -> String) -> String {
    do {
        return try callback(42)
    } catch {
        return "error"
    }
}

/// Accepts a throwing void closure (no return value).
/// Tests throwing closure with void return path.
public func callThrowingVoid(_ callback: @escaping () throws -> Void) -> Bool {
    do {
        try callback()
        return true
    } catch {
        return false
    }
}

/// Accepts a throwing closure returning Bool.
/// Tests the special bool marshalling path in throwing callbacks.
public func callThrowingBool(_ callback: @escaping (Int32) throws -> Bool) -> Bool {
    do {
        return try callback(10)
    } catch {
        return false
    }
}

/// Accepts a throwing closure returning a non-frozen struct (indirect return).
/// Exercises the combined indirect-return + error-out callback path: when the C#
/// delegate throws, the throwing-callback adapter must populate the Swift error-out
/// and the adapter must surface that error BEFORE `.move()`-ing the never-written
/// indirect result buffer. The thrown exception therefore unwinds through this
/// `catch` and yields the sentinel (-1, -1) — never a SIGABRT (managed exception
/// into native) or SIGSEGV (move of uninitialized storage). Graceful-fault guard
/// for the non-primitive/indirect closure-return shape.
public func callThrowingNonFrozenReturn(_ callback: @escaping () throws -> NonFrozenPoint) -> NonFrozenPoint {
    do {
        return try callback()
    } catch {
        return NonFrozenPoint(x: -1, y: -1)
    }
}

// MARK: - Returned Throwing Closure (Swift -> C# error path)

/// Error thrown by `makeAlwaysThrowingIntClosure`. Participates in the shared
/// allocation counters (see Lifetime/OwnershipTests.swift) so a C# leak test can
/// assert exactly how many error instances were created and how many survive —
/// i.e. whether the +1 the boundary retains on the way to C# is ever released.
public final class TrackedClosureError: Error {
    public let code: Int32

    public init(code: Int32) {
        self.code = code
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Returns a `() throws -> Int32` closure that always throws a fresh
/// `TrackedClosureError`. This is the Swift -> C# *returned*-throwing-closure
/// direction (C# invokes the closure), exercising
/// `ClosureEmitter.EmitThrowingClosureReturnMarshalling` — a supported shape
/// that currently has no end-to-end runtime coverage. The error-out the Swift
/// thunk hands back is `Unmanaged.passRetained` (+1); the C# side surfaces it as
/// `SwiftResult.FromFailure`, letting a leak test characterize whether that +1
/// is ever released.
public func makeAlwaysThrowingIntClosure() -> () throws -> Int32 {
    return { throw TrackedClosureError(code: 7) }
}

/// Returns a `() throws -> Int32` closure that never throws — it returns 99. This is
/// the success sibling of `makeAlwaysThrowingIntClosure`: the cdecl invoke thunk must
/// route the returned throwing closure through the CallConvCdecl invoker class (not the
/// inline CallConvSwift lambda that SIGSEGVs) and surface `SwiftResult.IsSuccess` with
/// the value. Durable guard for the wired-up returned-throwing-closure path.
public func makeNeverThrowingIntClosure() -> () throws -> Int32 {
    return { 99 }
}
