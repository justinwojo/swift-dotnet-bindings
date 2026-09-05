// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async methods carrying an Optional closure parameter
//
// ABI shape: an `async` method whose parameter list contains an `Optional`
// closure (a progress/event callback declared `((Int64, Int64) -> Void)?`),
// followed by a NON-optional trailing parameter. The trailing parameter is the
// observable: if the C# P/Invoke and the generated `@_cdecl` wrapper disagree on
// how many registers the optional closure occupies, every later register-passed
// argument (the trailing value, and `self`) shifts and the call corrupts.
//
// Provenance: image-loading and card-reader SDKs declare progress callbacks this
// way (`progressBlock: ((Int64, Int64) -> Void)? = nil`).

/// Reference-typed payload so an optional closure can carry a class argument.
public final class AsyncOptionalClosureToken {
    public let value: Int64
    public init(value: Int64) {
        self.value = value
    }
}

/// Class parent — instance and static methods.
public final class AsyncOptionalClosureCarrier {
    public init() {}

    /// Instance method: optional scalar closure followed by a non-optional trailing parameter.
    /// Suspends first so the callback fires after a real await point.
    public func sumWithProgress(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        progress?(base, trailing)
        return base + trailing
    }

    /// Same shape with a default value, so the callback can be omitted at the call site.
    public func sumWithDefaultedProgress(
        base: Int64,
        progress: ((Int64, Int64) -> Void)? = nil,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        progress?(base, trailing)
        return base + trailing
    }

    /// Optional closure carrying a class-typed argument.
    public func sumWithTokenCallback(
        base: Int64,
        notify: ((AsyncOptionalClosureToken) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        notify?(AsyncOptionalClosureToken(value: base + trailing))
        return base + trailing
    }

    /// Two optional closures back to back, then a non-optional trailing parameter.
    public func sumWithTwoProgressBlocks(
        base: Int64,
        first: ((Int64) -> Void)?,
        second: ((Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        first?(base)
        second?(trailing)
        return base + trailing
    }

    /// Static parent kind — no `self` register in play.
    public static func staticSumWithProgress(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        progress?(base, trailing)
        return base + trailing
    }

    /// Non-optional sibling of `sumWithProgress`. An `@escaping` closure is effectively escaping
    /// exactly as an Optional one is — the async body invokes it after the `@_cdecl` function has
    /// already returned — so it rides the SAME (funcPtr, context) carrier and the same
    /// Swift-ARC-owned handle lifetime. It is here as the shape that proves the carrier is keyed
    /// on escaping-ness rather than on Optional-ness: one mechanism serves both, so a future
    /// change that re-narrows it to Optionals only breaks this member too.
    public func sumWithEscapingProgress(
        base: Int64,
        progress: @escaping (Int64, Int64) -> Void,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        progress(base, trailing)
        return base + trailing
    }

    /// Reports the closure's NIL-NESS as observed on the Swift side, not merely whether the call
    /// survived. `progress?(…)` is a no-op for a nil closure, so a body that only invokes it
    /// returns the same value whether Swift saw a genuine `nil` or a non-nil closure that happens
    /// to do nothing — the register layout would be proven while the semantics were not. A consumer
    /// that branches on `progressBlock != nil` to skip expensive bookkeeping takes a different code
    /// path for each, so the null arm of the carrier has to reconstitute an ABSENT closure.
    ///
    /// The sign carries the observation and the magnitude keeps the register check, so one returned
    /// value asserts both. Defaulted so the same member covers the omitted call site too.
    public func observedNilnessSum(
        base: Int64,
        progress: ((Int64, Int64) -> Void)? = nil,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        let observedNil = (progress == nil)
        progress?(base, trailing)
        return (observedNil ? -1 : 1) * (base + trailing)
    }

    /// Static parent kind for the same nil-ness observation — no `self` register in play.
    public static func staticObservedNilnessSum(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        let observedNil = (progress == nil)
        progress?(base, trailing)
        return (observedNil ? -1 : 1) * (base + trailing)
    }

    /// Throwing + a suspension long enough for cancellation to land while the optional-closure
    /// carrier is still live, but short enough that the uncancelled positive control is cheap.
    public func cancellableSumWithProgress(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async throws -> Int64 {
        try await Task.sleep(nanoseconds: 1_500_000_000)
        progress?(base, trailing)
        return base + trailing
    }
}

/// Struct parent — same shape over a value-typed receiver.
public struct AsyncOptionalClosureValueCarrier {
    public let bias: Int64

    public init(bias: Int64) {
        self.bias = bias
    }

    public func sumWithProgress(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        progress?(base, trailing)
        return bias + base + trailing
    }

    /// Value-typed receiver flavour of the nil-ness observation: the sign reports what Swift saw,
    /// the magnitude folds in the stored `bias` so a corrupted `self` is visible in the same value.
    public func observedNilnessSum(
        base: Int64,
        progress: ((Int64, Int64) -> Void)?,
        trailing: Int64
    ) async -> Int64 {
        try? await Task.sleep(nanoseconds: 20_000_000)
        let observedNil = (progress == nil)
        progress?(base, trailing)
        return (observedNil ? -1 : 1) * (bias + base + trailing)
    }
}
