// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// An `@objc` protocol (not refining NSObjectProtocol) that pairs completion-handler requirements
// with their `async` twins, the shape SDK-style service protocols use to publish both calling
// styles to Objective-C.
//
// Two separate things have to survive into the synthesized witnesses:
//   * `async`. For a pure-Swift protocol a sync witness satisfies an async requirement, but an
//     `@objc` async requirement is bridged to a completion-handler selector and swiftc accepts only
//     an async candidate. That holds whichever carrier class emits the conformance.
//   * Closure attributes carried by a typealias. `StorefrontCompletion` is
//     `@MainActor @Sendable (String?) -> Void`; the ABI JSON desugars the alias and drops both
//     attributes, so a witness spelled from the ABI type alone declares a different closure type.
//     The same attributes written inline must keep working too, and an optional alias-typed
//     closure carries them on the wrapped function type.

import Foundation

public typealias StorefrontCompletion = @MainActor @Sendable (String?) -> Void

@objc public protocol StorefrontService {
    @objc func storefront(completion: @escaping StorefrontCompletion)
    @objc func storefront() async -> String?
    @objc func countryCode(for region: String, completion: @escaping @MainActor @Sendable (String?) -> Void)
    @objc func logIn(_ userID: String) async throws -> String
    @objc func productCount() async -> Int
    @objc func refresh(completion: StorefrontCompletion?)
    @objc func regionCount() -> Int
}

/// Calls the synchronous requirement through the existential, which only dispatches once the whole
/// conformance, async witnesses included, compiles.
public func storefrontServiceRegionCount(_ service: any StorefrontService) -> Int {
    service.regionCount()
}
