// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async existential-array parameters (Issue #34)
//
// Repro for an async method whose parameter is `[any Proto]`. These shapes round-trip
// successfully through
// the async wrapper path today — the C# P/Invoke surface is uniformly blittable
// (callback + taskId + IntPtr parts array) because async methods early-return
// `HasNonBlittablePInvokeTypes = false`. SB0001 should NOT fire on the generated
// async method.
//
// A matching sync-with-closure shape is included so we can also observe the genuine
// JIT-risk path (existential-array parameter on a sync method) and confirm SB0001
// still fires there — this is the distinguishing case the reporter's single
// diagnostic can't separate from the working async shape.

/// Marker protocol for elements of existential-array parameters.
public protocol PartsRepresentable {
    var label: String { get }
}

/// Concrete conformer.
public struct TextPart: PartsRepresentable {
    public let label: String
    public init(label: String) { self.label = label }
}

/// Class with an async instance method taking a heterogeneous array of existentials
/// and returning a value by awaiting a fake remote call.
public final class GenerateContentClient {
    public init() {}

    /// Working async shape: takes `[any PartsRepresentable]`, returns a String.
    /// No SB0001 should be emitted on the generated surface.
    public func generateContentAsync(parts: [any PartsRepresentable]) async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return parts.map { $0.label }.joined(separator: ",")
    }

    /// Broken stream shape: sync method taking an escaping closure whose param is
    /// `[any PartsRepresentable]`. This is the genuinely JIT-risky shape — the
    /// existential-array param flows through a @convention(c) callback that can't
    /// spell it in Swift. SB0001 is the correct outcome here.
    public func generateContentStream(
        parts: [any PartsRepresentable],
        onChunk: @escaping ([any PartsRepresentable]) -> Void
    ) {
        onChunk(parts)
    }
}

/// Free function form — also async with existential-array param, to confirm the
/// classifier doesn't hinge on instance-vs-free-function for this shape.
public func generateContentFreeAsync(parts: [any PartsRepresentable]) async -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return Int32(parts.count)
}
