// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Closure-carrying initializers: the nested-closure bridge in constructor position.
//
// Shape observed in a payments SDK's deferred-intent configuration type — the type is
// constructed ONLY through `init(mode:confirmHandler:)`, where `confirmHandler` is an
// escaping closure that itself receives a completion callback. The nested-closure bridge
// already handles that exact parameter shape on an ordinary method, but constructors were
// excluded from every closure bridge, so the initializer fell through to the visible-but-
// throwing closure-parameter tombstone. The type then bound as a shell: present in the C#
// surface, with no way to build one, taking every downstream member that needs it with it.
//
// The `Deferred*` types below pin the recovery end to end: the initializer emits as a real
// C# constructor, the outer closure fires with the caller's arguments, and the inner
// completion callback carries a value back into Swift.

import Foundation

// MARK: - Recovered: escaping outer closure with an escaping inner completion

/// Configuration whose ONLY initializer carries a callback-bearing closure. The handler is
/// stored, so the outer closure is escaping and its GCHandle context has to survive the
/// constructor call under Swift ARC ownership.
public final class DeferredIntentConfiguration {
    public let mode: Int32
    private let confirmHandler: (Int32, @escaping (Int32) -> Void) -> Void

    public init(mode: Int32, confirmHandler: @escaping (Int32, @escaping (Int32) -> Void) -> Void) {
        self.mode = mode
        self.confirmHandler = confirmHandler
    }

    /// Drives the stored handler so a C# caller can observe both directions of the bridge:
    /// the outer closure receiving `amount`, and the inner completion carrying a value back.
    public func confirm(amount: Int32) -> Int32 {
        var captured: Int32 = -1
        confirmHandler(amount) { result in captured = result }
        return captured
    }

    /// Second call through the same stored handler — proves the closure context outlives the
    /// initializer rather than being freed when the constructor returns.
    public func confirmTwice(amount: Int32) -> Int32 {
        confirm(amount: amount) + confirm(amount: amount)
    }
}

/// Downstream surface that only becomes reachable once the configuration above is
/// constructible — the "stranded type graph" half of the reported shape.
public final class DeferredIntentController {
    private let configuration: DeferredIntentConfiguration

    public init(configuration: DeferredIntentConfiguration) {
        self.configuration = configuration
    }

    public func run(amount: Int32) -> Int32 {
        configuration.confirm(amount: amount)
    }

    public func configuredMode() -> Int32 {
        configuration.mode
    }
}

// MARK: - Recovered: non-escaping outer closure alongside an ordinary parameter

/// Initializer whose callback-bearing closure is invoked during construction and never
/// stored, so the outer closure is non-escaping. Pins the branch where the GCHandle is
/// freed unconditionally on return rather than handed to a Swift-ARC owner box.
public final class ImmediateConfirmationConfiguration {
    public let resolved: Int32

    public init(seed: Int32, resolve: (Int32, @escaping (Int32) -> Void) -> Void) {
        var value: Int32 = 0
        resolve(seed) { result in value = result }
        self.resolved = value
    }
}
