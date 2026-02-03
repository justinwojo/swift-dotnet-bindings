// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @MainActor Class

/// A class annotated with @MainActor, requiring main-thread execution.
/// Tests: @MainActor isolation on entire class.
/// Expected C#: Class with dispatch wrapper or [MainActor] attribute.
@MainActor
public class MainActorViewModel {
    public var title: String
    public var count: Int32

    public init(title: String) {
        self.title = title
        self.count = 0
    }

    /// Synchronous method on @MainActor class.
    public func increment() -> Int32 {
        count += 1
        return count
    }

    /// Async method on @MainActor class.
    public func refreshTitle() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return "Refreshed: \(title)"
    }

    /// Computed property on @MainActor class.
    public var summary: String {
        return "\(title): \(count)"
    }
}

// MARK: - @MainActor Method

/// A regular struct with individual @MainActor-annotated methods.
/// Tests: Per-method @MainActor isolation.
public struct MainActorMethods {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Method isolated to the main actor.
    @MainActor
    public func mainActorMethod() -> String {
        return "MainActor: \(value)"
    }

    /// Non-isolated method for comparison.
    public func regularMethod() -> String {
        return "Regular: \(value)"
    }
}

// MARK: - Free Functions

/// Free function isolated to @MainActor.
@MainActor
public func mainActorFreeFunction() -> String {
    return "MainActor free function"
}
