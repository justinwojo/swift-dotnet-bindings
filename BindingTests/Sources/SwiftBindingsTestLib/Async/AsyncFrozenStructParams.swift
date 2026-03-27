// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Async methods with frozen struct parameters — exercises the heap allocation fix
// for frozen blittable struct params in async methods. Before the fix, the generator
// used stackalloc for these params, which is unsafe across await boundaries.

/// Async method accepting a single frozen struct param and returning a String
public func asyncProcessFrozenPoint(_ point: FrozenPoint) async -> String {
    return "(\(point.x), \(point.y))"
}

/// Async method accepting a frozen struct param and returning a frozen struct
public func asyncScaleFrozenPoint(_ point: FrozenPoint, by factor: Double) async -> FrozenPoint {
    return FrozenPoint(x: point.x * factor, y: point.y * factor)
}

/// Async method with multiple frozen struct params
public func asyncCombineFrozenPoints(_ a: FrozenPoint, _ b: FrozenPoint) async -> FrozenPoint {
    return FrozenPoint(x: a.x + b.x, y: a.y + b.y)
}

/// Async throwing method with frozen struct param
public func asyncValidateFrozenPoint(_ point: FrozenPoint) async throws -> Bool {
    return point.x >= 0 && point.y >= 0
}
