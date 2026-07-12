// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol for Existential Boxing Tests

/// Protocol for testing concrete types passed as protocol existential params.
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

/// Resilient (non-`@frozen`) struct with non-trivial defaulted state — a `String`, an
/// `Int32`, and an `Optional<Int32>` — behind a parameterless `init()`. This mirrors
/// CryptoSwift's resilient `ECB` block mode exactly: `init()` with defaulted non-trivial
/// fields (`options`, `customBlockSize: Int?`). Unlike the trivial `SimpleMode`/`StrictMode`
/// (which box inline), this payload is non-bitwise-trivial and forces the existential value
/// out of line through `swift_allocBox`. That owns=true, non-inline box is the exact shape
/// that SIGKILLed a protocol-typed constructor argument on NativeAOT
/// (`new AES(key, new ECB(), ...)`). `modeName`/`validate` read the stored state, so a
/// corrupted boxed payload is observable as a wrong value (sim) rather than only a crash.
public struct RichMode: ProcessingMode {
    private let note: String
    private let threshold: Int32
    private let ceiling: Int32?

    public init() {
        self.note = "rich"
        self.threshold = 10
        self.ceiling = 100
    }

    public var modeName: String { note }
    public func validate(input: Int32) -> Bool {
        if let ceiling { return input >= threshold && input <= ceiling }
        return input >= threshold
    }
}

/// Same non-inline resilient shape as `RichMode`, but its `Optional<Int32>` field is `nil` —
/// exercises the other Optional-payload branch through the boxed existential.
public struct OpenMode: ProcessingMode {
    private let note: String
    private let threshold: Int32
    private let ceiling: Int32?

    public init() {
        self.note = "open"
        self.threshold = 10
        self.ceiling = nil
    }

    public var modeName: String { note }
    public func validate(input: Int32) -> Bool {
        if let ceiling { return input >= threshold && input <= ceiling }
        return input >= threshold
    }
}

/// Reference-type (`class`) conformer — the control for the struct boxing path. A class
/// existential bridges through ARC rather than an inline/allocBox value copy, mirroring
/// CryptoSwift's class block modes (`GCM`/`OCB`).
public final class ClassMode: ProcessingMode {
    private let name: String
    private let floor: Int32

    public init() {
        self.name = "cls"
        self.floor = 5
    }

    public var modeName: String { name }
    public func validate(input: Int32) -> Bool { input >= floor }
}

// MARK: - Class Taking Protocol Existential Parameter

/// Class taking a protocol existential parameter.
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

    /// Sibling method that also boxes a protocol existential argument — exercises the
    /// same owns=true boxing path as the constructor, but at a method call site.
    public func matches(other: any ProcessingMode, value: Int32) -> Bool {
        return mode.validate(input: value) == other.validate(input: value)
    }
}

// MARK: - Multi-param Constructor with Existential

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

// MARK: - N2: Protocol Methods Accepting Existential Parameters

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
