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

// MARK: - Bug 3 Case 1: existential-param + completion handler (Stripe shape)

/// Models the Stripe Payments shape that originally leaked an existential heap
/// allocation per call: a method whose non-closure parameter list contains an
/// `any Protocol` existential, paired with a trailing `@escaping (T) -> Void`
/// completion handler. The OLD generator emitted a duplicated `…Async` overload
/// that allocated `existentialContextHeap` and never freed it. The fix in
/// `MethodHandler.TryEmitCompletionHandlerOverload` makes the async overload
/// delegate to the sync method (which does free the heap in its `finally`),
/// so this fixture's `…Async` overload should compile and run cleanly.
public protocol Bug3PaymentContext {
    var contextLabel: String { get }
}

/// Default implementation used by the Bug 3 Case 1 test.
public final class Bug3DefaultPaymentContext: Bug3PaymentContext {
    public init() {}
    public var contextLabel: String { "ctx-default" }
}

public final class Bug3CompletionFixture {
    public init() {}

    /// Stripe-shaped: `(any Protocol, @escaping (Int32) -> Void) -> Void`. Generator
    /// emits a callback overload (used to allocate + free the existential) plus a
    /// Task-returning overload that delegates to it.
    public func processPayment(
        with context: any Bug3PaymentContext,
        completion: @escaping (Int32) -> Void
    ) {
        completion(Int32(context.contextLabel.utf8.count))
    }
}
