// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Async INSTANCE methods on a class that take frozen struct parameters.
// Exercises the heap allocation fix for frozen blittable struct params in
// async instance methods — the 3 instance method branches that were untested.
// Session 5's fix covered free/static functions only.

/// Class with async instance methods that accept FrozenPoint parameters.
/// Tests the instance method path for async frozen struct marshalling.
public class PointProcessor {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Async instance method with a single frozen struct param.
    public func processPoint(_ point: FrozenPoint) async -> String {
        return "\(label): (\(point.x), \(point.y))"
    }

    /// Async instance method with frozen struct param returning frozen struct.
    public func scalePoint(_ point: FrozenPoint, by factor: Double) async -> FrozenPoint {
        return FrozenPoint(x: point.x * factor, y: point.y * factor)
    }

    /// Async instance method with multiple frozen struct params.
    public func addPoints(_ a: FrozenPoint, _ b: FrozenPoint) async -> FrozenPoint {
        return FrozenPoint(x: a.x + b.x, y: a.y + b.y)
    }

    /// Async throwing instance method with frozen struct param.
    public func validatePoint(_ point: FrozenPoint) async throws -> Bool {
        return point.x >= 0 && point.y >= 0
    }
}
