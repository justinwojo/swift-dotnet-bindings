// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Completion Handler Patterns (Stripe, Alamofire)

/// Class with completion handler methods that qualify for Task conversion.
/// CompletionHandlerDetector converts trailing @escaping ()->Void closures
/// on void-returning methods into async Task overloads.
public class CompletionService {
    public init() {}

    /// Standard completion handler (void result).
    /// Shape: VoidResult — () -> Void
    public func fetchData(completion: @escaping () -> Void) {
        completion()
    }

    /// Completion handler with single result.
    /// Shape: SingleResult — (Int32) -> Void
    public func fetchValue(completion: @escaping (Int32) -> Void) {
        completion(42)
    }

    /// Completion handler with two parameters (result + success flag).
    /// Shape: detected as (Int32, Bool) -> Void — 2-param non-error pattern.
    public func fetchWithFlag(completion: @escaping (Int32, Bool) -> Void) {
        completion(100, true)
    }
}

/// Free function with completion handler (void result).
/// Tests that free functions also qualify for Task conversion.
public func performAction(completion: @escaping () -> Void) {
    completion()
}

/// Free function with single-result completion handler.
public func computeValue(completion: @escaping (Int32) -> Void) {
    completion(99)
}
