// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async methods returning opaque types (`async -> some Protocol`)
//
// Regression coverage for the async/throwing opaque-return emission gate. A method that is
// BOTH async (and/or throwing) AND returns `some Protocol` must be emitted ONLY by the async
// harness (which boxes the opaque return into an `any Protocol` existential). The thin
// `@_silgen_name` opaque-return alias emits a synchronous `return self.method()` with no
// `try`/`await`, so emitting it alongside the async harness double-defines the shared
// `{mangled}_async` symbol and fails to compile ("'async' call …" / "call can throw …").
// This is the exact shape of AppIntents `perform() async throws -> some IntentResult`.
//
// Reuses `Describable` (Protocols/BasicProtocols.swift) + `SimpleItem` (Protocols/Conformance.swift).

public struct AsyncOpaqueWorker {
    public init() {}

    /// `async -> some Describable`: async harness boxes the opaque return to `any Describable`.
    public func makeOpaqueAsync(text: String) async -> some Describable {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return SimpleItem(id: "async-opaque", label: text)
    }

    /// `async throws -> some Describable`: the AppIntents `perform()` shape that triggered the
    /// double-emit compile failure. Only the async harness should emit; the sync alias must skip.
    public func makeOpaqueAsyncThrowing(text: String) async throws -> some Describable {
        try await Task.sleep(nanoseconds: 1_000_000)
        return SimpleItem(id: "async-throwing-opaque", label: text)
    }

    /// `throws -> some Describable` (non-async): the throwing arm of the same gate. The sync
    /// alias would emit `return self.method()` without `try` → "call can throw" compile error.
    public func makeOpaqueThrowing(text: String) throws -> some Describable {
        return SimpleItem(id: "throwing-opaque", label: text)
    }
}
