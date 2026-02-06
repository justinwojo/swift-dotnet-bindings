// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Synthetic SwiftUI Views for testing SwiftUI bridge parameter types.
// Each View exercises one parameter kind from the v2 bridge emitter.

import SwiftUI

// MARK: - Supporting Types

/// Simple enum for BoundEnum parameter testing.
public enum AlertStyle: Int32 {
    case info = 0
    case warning = 1
    case error = 2
}

/// Simple class for BoundType parameter testing.
/// Includes deinit counter for lifetime validation.
public class SimpleModel {
    public static var deinitCount: Int32 = 0

    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func getValue() -> Int32 {
        return value
    }

    deinit {
        SimpleModel.deinitCount += 1
    }
}

// MARK: - Test Views

/// Tests BoundEnum parameter kind.
public struct EnumParamView: View {
    public let style: AlertStyle

    public init(style: AlertStyle) {
        self.style = style
    }

    public var body: some View {
        Text("EnumParam: \(style.rawValue)")
    }
}

/// Tests BoundType (class) parameter kind.
public struct ClassParamView: View {
    public let model: SimpleModel

    public init(model: SimpleModel) {
        self.model = model
    }

    public var body: some View {
        Text("ClassParam: \(model.getValue())")
    }
}

/// Tests TypedClosure: (Int32) -> Bool parameter kind.
public struct TypedClosureView: View {
    public let onValue: (Int32) -> Bool

    public init(onValue: @escaping (Int32) -> Bool) {
        self.onValue = onValue
    }

    public var body: some View {
        Text("TypedClosure")
    }
}

/// Tests multi-arg TypedClosure: (Int32, Bool) -> Void parameter kind.
public struct MultiArgClosureView: View {
    public let onEvent: (Int32, Bool) -> Void

    public init(onEvent: @escaping (Int32, Bool) -> Void) {
        self.onEvent = onEvent
    }

    public var body: some View {
        Text("MultiArgClosure")
    }
}

/// Tests mixed parameters: BoundEnum + void closure + primitive.
public struct MixedParamView: View {
    public let style: AlertStyle
    public let onAction: () -> Void
    public let count: Int32

    public init(style: AlertStyle, onAction: @escaping () -> Void, count: Int32) {
        self.style = style
        self.onAction = onAction
        self.count = count
    }

    public var body: some View {
        Text("Mixed: \(style.rawValue), \(count)")
    }
}

/// Tests Optional<BoundEnum> parameter kind.
public struct OptionalEnumView: View {
    public let style: AlertStyle?

    public init(style: AlertStyle? = nil) {
        self.style = style
    }

    public var body: some View {
        Text("OptionalEnum: \(style?.rawValue ?? -1)")
    }
}

/// Tests Optional<BoundType> (class) parameter kind.
public struct OptionalClassView: View {
    public let model: SimpleModel?

    public init(model: SimpleModel? = nil) {
        self.model = model
    }

    public var body: some View {
        Text("OptionalClass: \(model?.getValue() ?? -1)")
    }
}

// MARK: - Async Chain Test Types (Phase 2B)

/// Async service class for testing single-level async chain inference.
/// init(key:) is `async throws` — the bridge must construct this in a Task.
public class AsyncService {
    public let key: String

    public init(key: String) async throws {
        // Simulate async initialization (e.g. network validation)
        try await Task.sleep(nanoseconds: 1_000)
        self.key = key
    }

    public func getKey() -> String { key }
}

/// View with a single async dependency: AsyncService.
/// Inferred chain: AsyncService(key:) async throws → AsyncServiceView(service:)
public struct AsyncServiceView: View {
    public let service: AsyncService

    public init(service: AsyncService) {
        self.service = service
    }

    public var body: some View {
        Text("AsyncService: \(service.getKey())")
    }
}

/// Intermediate class that depends on AsyncService — for testing deep chains.
/// init(service:, mode:) is synchronous but takes an async dependency.
public class Processor {
    public let service: AsyncService
    public let mode: Int32

    public init(service: AsyncService, mode: Int32) {
        self.service = service
        self.mode = mode
    }

    public func getMode() -> Int32 { mode }
}

/// View with a two-level async chain.
/// Inferred chain: AsyncService(key:) async throws → Processor(service:, mode:) → DeepChainView(processor:)
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
/// This tests that non-chain leaf params are correctly passed through to the View init.
/// Inferred chain: AsyncService(key:) async throws → MixedAsyncView(service:, count:, enabled:)
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
        Text("Mixed: \(service.getKey()) count=\(count) enabled=\(enabled)")
    }
}
