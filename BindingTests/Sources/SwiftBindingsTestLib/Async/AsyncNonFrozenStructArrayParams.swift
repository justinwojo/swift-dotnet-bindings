// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async + Array<NonFrozenStruct> param lifetime
//
// The C# bridge serializes the IEnumerable<TStruct> into a SwiftArray
// container whose payload buffer the wrapper dereferences from the Swift
// async continuation. Each fixture sleeps briefly inside the async body so
// the continuation is guaranteed to suspend back to the runtime — i.e. the
// C# foreground frame has unwound before Swift reads the buffer.

/// Async version of `sumPointMagnitudes`. Mirrors the MusicKit
/// `MusicPlayer.Queue.insert(_ entries: [Album], position:) async` shape
/// — async free function taking `Array<TStruct>` where `TStruct` is a
/// non-frozen struct that the C# side surfaces as a SafeHandle-backed
/// reference type.
public func sumPointMagnitudesAsync(_ points: [NonFrozenPoint]) async -> Double {
    // Force a real suspension so the foreground C# frame unwinds before
    // the continuation reads `points`. Pre-fix, the buffer would be disposed.
    try? await Task.sleep(nanoseconds: 1_000_000)
    return points.reduce(0.0) { acc, p in acc + p.distanceFromOrigin() }
}

/// Async round-trip variant — exercises Array<TStruct> as both an async
/// parameter and an async return value. Validates the entire continuation
/// path holds the buffer live across the suspension point.
public func scalePointsAsync(_ points: [NonFrozenPoint], by factor: Double) async -> [NonFrozenPoint] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return points.map { NonFrozenPoint(x: $0.x * factor, y: $0.y * factor) }
}
