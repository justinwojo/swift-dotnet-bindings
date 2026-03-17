// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol for Existential Boxing Tests

/// Protocol for testing concrete types passed as protocol existential params.
/// Real-world pattern: CryptoSwift AES(key, ECB) where ECB is concrete struct passed as `any BlockMode`.
public protocol ProcessingMode {
    var modeName: String { get }
    func validate(input: Int32) -> Bool
}

// MARK: - Concrete Types Conforming to ProcessingMode

/// Simple processing mode — accepts any non-negative input.
public struct SimpleMode: ProcessingMode {
    public var modeName: String { "simple" }
    public init() {}
    public func validate(input: Int32) -> Bool { input >= 0 }
}

/// Strict processing mode — accepts only values in (0, 1000).
public struct StrictMode: ProcessingMode {
    public var modeName: String { "strict" }
    public init() {}
    public func validate(input: Int32) -> Bool { input > 0 && input < 1000 }
}

// MARK: - Class Taking Protocol Existential Parameter

/// Class taking protocol existential param — the CryptoSwift AES(key, ECB) pattern.
public class ModeProcessor {
    private let mode: any ProcessingMode

    public init(mode: any ProcessingMode) {
        self.mode = mode
    }

    public func process(value: Int32) -> Bool {
        return mode.validate(input: value)
    }

    public func getModeName() -> String {
        return mode.modeName
    }
}

// MARK: - Multi-param Constructor with Existential (CryptoSwift AES pattern)

/// Constructor combining collection + protocol existential.
public class Pipeline {
    private let steps: [Int32]
    private let mode: any ProcessingMode

    public init(steps: [Int32], mode: any ProcessingMode) {
        self.steps = steps
        self.mode = mode
    }

    public func stepCount() -> Int32 { Int32(steps.count) }
    public func getModeName() -> String { mode.modeName }
}

// MARK: - Free Functions with Existential Params

/// Free function with existential param.
public func runWithMode(_ mode: any ProcessingMode, value: Int32) -> Bool {
    return mode.validate(input: value)
}

/// Two existential params.
public func compareResults(_ a: any ProcessingMode, _ b: any ProcessingMode, value: Int32) -> Bool {
    return a.validate(input: value) == b.validate(input: value)
}

// MARK: - N2: Protocol Methods Accepting Existential Parameters (Parchment pattern)

/// Protocol whose methods take existential parameters.
public protocol ModeConsumer {
    func consume(mode: any ProcessingMode) -> String
}

/// Concrete type conforming to ModeConsumer.
public struct SimpleModeConsumer: ModeConsumer {
    public init() {}

    public func consume(mode: any ProcessingMode) -> String {
        return "Consumed: \(mode.modeName)"
    }
}

/// Free function exercising ModeConsumer with existential params.
public func runModeConsumer(_ consumer: any ModeConsumer, mode: any ProcessingMode) -> String {
    return consumer.consume(mode: mode)
}
