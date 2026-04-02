// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Async chain SwiftUI Views for bridge async inference testing.
// Each View has constructor dependencies that require async construction chains.

import SwiftUI

/// View with a single async dependency: AsyncService.
/// Inferred chain: AsyncService(key:) async throws -> AsyncServiceView(service:)
public struct AsyncServiceView: View {
    public let service: AsyncService

    public init(service: AsyncService) {
        self.service = service
    }

    public var body: some View {
        Text("AsyncService: \(service.getKey())")
    }
}

/// View with a two-level async chain.
/// Inferred chain: AsyncService(key:) async throws -> Processor(service:, mode:) -> DeepChainView(processor:)
public struct DeepChainView: View {
    public let processor: Processor

    public init(processor: Processor) {
        self.processor = processor
    }

    public var body: some View {
        Text("DeepChain: \(processor.getMode())")
    }
}

/// View with mixed chain + leaf params: async dependency AND direct primitive/bool params.
/// Inferred chain: AsyncService(key:) async throws -> MixedAsyncView(service:, count:, enabled:)
public struct MixedAsyncView: View {
    public let service: AsyncService
    public let count: Int32
    public let enabled: Bool

    public init(service: AsyncService, count: Int32, enabled: Bool) {
        self.service = service
        self.count = count
        self.enabled = enabled
    }

    public var body: some View {
        Text("Mixed: \(service.getKey()) count=\(count) enabled=\(enabled ? "true" : "false")")
    }
}
