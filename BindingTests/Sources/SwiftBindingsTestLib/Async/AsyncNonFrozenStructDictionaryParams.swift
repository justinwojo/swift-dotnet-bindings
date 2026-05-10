// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async + Dictionary<String, NonFrozenStruct> param lifetime
//
// Sister fixture to AsyncNonFrozenStructArrayParams and the existing
// SetMembershipCountAsync coverage — locks down the third
// IDisposable serialization container (SwiftDictionary<K,V>) on the
// async hand-off path, so all three branches (Array / Set / Dictionary)
// of RequiresAsyncDeferredDisposeList have a runtime repro.

/// Async dictionary parameter with non-frozen struct values. The async
/// body suspends so the C# foreground frame unwinds before the Swift
/// continuation reads the buffer — without the deferred-dispose hand-off
/// the SwiftDictionary container's `using var` would dispose the buffer
/// the moment the foreground wrapper returns `tcs.Task`.
public func countPointsWithMagnitudeAsync(_ points: [String: NonFrozenPoint], atLeast threshold: Double) async -> Int {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return points.values.reduce(0) { acc, p in
        acc + (p.distanceFromOrigin() >= threshold ? 1 : 0)
    }
}
