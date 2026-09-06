// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Closure delegate-type parity
//
// The public C# delegate type declared for a closure parameter and the type the
// [UnmanagedCallersOnly] trampoline casts the stored delegate back to
// (`SwiftClosureMarshaller.GetDelegateFrom(Boxed)Context<T>`) must be ONE computation.
// When they were computed by two independent translators, a callback whose argument or
// return used a container-shaped type stored `Action<A>` and cast to `Action<B>`, so the
// very first invocation threw InvalidCastException inside the trampoline and became a
// FailFastUnhandledClosureException — a process abort on any callback.
//
// The shapes below are the ones where the two translators disagreed:
//   * `Result<T?, any Error>` — the failure arm resolved to the raw existential carrier on
//     one side and to the well-known `Swift.Error` mapping on the other.
//   * `[T]` / `[K: V]` in callback argument and callback return position — one side used
//     the idiomatic collection interface, the other the Swift container carrier.
//
// Each entry delivers its callbacks eagerly (synchronously, before returning) so a test can
// assert exactly-once delivery and a readable payload without any scheduling.

/// Payload class handed back through a `Result` success arm.
public class ParityPayload {
    public let label: String
    public let magnitude: Int32
    public init(label: String, magnitude: Int32) {
        self.label = label
        self.magnitude = magnitude
    }
}

/// Error delivered through a `Result` failure arm. It carries a `code` so a test can tell WHICH
/// error arrived rather than only that some error did: the bridged `AnyError.LocalizedDescription`
/// renders the boxed error with Swift's `String(describing:)`, which for a struct spells out the
/// type name and its stored properties. The `LocalizedError` conformance mirrors what a real library
/// would write; it is not what makes the code readable from C#.
public struct ParityFailure: LocalizedError {
    public let code: Int32
    public init(code: Int32) {
        self.code = code
    }
    public var errorDescription: String? { "parity-failure-\(code)" }
}

/// Frozen-ish struct element used for the `[Struct]` closure-return shape.
public struct ParityPoint {
    public let x: Double
    public let y: Double
    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

// MARK: - Result<Optional<Class>, any Error> callbacks (class parent)

public class ResultOptionalCallbackHost {
    public init() {}

    /// Delivers the success arm with a non-nil payload, exactly once, before returning.
    public func deliverSome(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.success(ParityPayload(label: "some", magnitude: 41)))
    }

    /// Delivers the success arm carrying `nil` — the Optional-inside-Result arm.
    public func deliverNone(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.success(nil))
    }

    /// Delivers the failure arm, exactly once, before returning.
    public func deliverFailure(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.failure(ParityFailure(code: 7)))
    }

    /// Static parent-position variant — the emission path differs from the instance one.
    public static func deliverSomeStatic(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.success(ParityPayload(label: "static", magnitude: 5)))
    }

    /// Static failure variant.
    public static func deliverFailureStatic(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.failure(ParityFailure(code: 9)))
    }
}

// MARK: - Result<Optional<Class>, any Error> callbacks (struct parent)

public struct ResultOptionalCallbackStruct {
    public init() {}

    public func deliverSome(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.success(ParityPayload(label: "struct-some", magnitude: 13)))
    }

    public func deliverFailure(_ completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        completion(.failure(ParityFailure(code: 21)))
    }
}

// MARK: - Result<Any?, any Error> callbacks
//
// The exact reported shape: the success arm is a bare `Any?` (an opaque existential carrier)
// rather than a bound class, which routes the member through the wrapper-emitter closure path
// instead of the method-closure bridge. This is the pairing where the two former translators
// disagreed on the FAILURE arm — one spelled it as the raw existential carrier, the other as
// the well-known `Swift.Error` mapping.

public class AnyResultCallbackHost {
    public init() {}

    /// Success arm carrying a boxed value.
    public func deliverAnySome(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.success(Int32(64)))
    }

    /// Success arm carrying `nil`.
    public func deliverAnyNone(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.success(nil))
    }

    /// Failure arm.
    public func deliverAnyFailure(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.failure(ParityFailure(code: 33)))
    }

    /// Static variant — a different emission path from the instance one.
    public static func deliverAnySomeStatic(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.success(Int32(65)))
    }
}

public struct AnyResultCallbackStruct {
    public init() {}

    public func deliverAnySome(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.success(Int32(66)))
    }

    public func deliverAnyFailure(_ completion: @escaping (Result<Any?, Error>) -> Void) {
        completion(.failure(ParityFailure(code: 34)))
    }
}

/// The same `Result<Optional<T>, any Error>` callback in CONSTRUCTOR position, which the
/// method-closure bridge never claims. These fall to the ordinary wrapper-emitter closure path —
/// the path where the public delegate type and the trampoline cast were computed by two different
/// translators, so the stored `Action<SwiftResult<…, ExistentialContainer1>>` was recovered as
/// `Action<SwiftResult<…, AnyError>>` and every invocation aborted the process.
///
/// Each initializer delivers exactly one callback, synchronously, before `self` is fully returned.
public class WrapperPathResultHost {
    /// The mode the instance was built with, readable after construction so a test can confirm the
    /// object is usable once the callback has run rather than only that the callback fired.
    public let deliveredCode: Int32

    /// Bound-class success arm, so a test can read the payload back rather than only observing that
    /// the success case arrived. `mode` selects the arm — 0 delivers a payload, 1 delivers `nil`,
    /// anything else delivers the failure — and also gives the initializer a C# signature distinct
    /// from its sibling below.
    public init(mode: Int32, completion: @escaping (Result<ParityPayload?, Error>) -> Void) {
        deliveredCode = mode
        switch mode {
        case 0:
            completion(.success(ParityPayload(label: "wrapper-path", magnitude: 11)))
        case 1:
            completion(.success(nil))
        default:
            completion(.failure(ParityFailure(code: mode)))
        }
    }

    /// The exact reported shape: an opaque `Any?` success arm. 0 delivers the success case, anything
    /// else the failure case.
    public init(anyMode: Int32, completion: @escaping (Result<Any?, Error>) -> Void) {
        deliveredCode = anyMode
        if anyMode == 0 {
            completion(.success(Int32(77)))
        } else {
            completion(.failure(ParityFailure(code: anyMode)))
        }
    }
}

// MARK: - Collection-shaped callback arguments and returns

public class CollectionCallbackHost {
    public init() {}

    /// Callback ARGUMENT is `[Double]`. Swift builds the array and hands it over.
    public func emitDoubles(_ sink: @escaping ([Double]) -> Void) {
        sink([1.5, 2.5, 3.5])
    }

    /// Callback ARGUMENT is `[Struct]`.
    public func emitPoints(_ sink: @escaping ([ParityPoint]) -> Void) {
        sink([ParityPoint(x: 1, y: 2), ParityPoint(x: 3, y: 4)])
    }

    /// Callback RETURN is `[Double]`: Swift invokes the C# lambda and consumes the result
    /// itself, so the assertion below observes the bridge rather than the lambda. Returns the
    /// sum Swift computed over the values the lambda produced.
    public func sumProduced(_ block: (Double) -> [Double]) -> Double {
        var total = 0.0
        for seed in [1.0, 2.0] {
            for value in block(seed) {
                total += value
            }
        }
        return total
    }

    /// Callback RETURN is `[Struct]`, consumed on the Swift side.
    public func sumPointsProduced(_ block: (Double) -> [ParityPoint]) -> Double {
        var total = 0.0
        for point in block(2.0) {
            total += point.x + point.y
        }
        return total
    }

    /// Callback ARGUMENT is `[String: Int32]` — the dictionary counterpart of `emitDoubles`.
    public func emitCounts(_ sink: @escaping ([String: Int32]) -> Void) {
        sink(["a": 1, "b": 2])
    }
}

// MARK: - inout closure argument (declined, not bridged)

public class DictionaryBuilderHost {
    public init() {}

    /// The closure receives the builder dictionary `inout` and Swift reads it back afterwards, so a
    /// value the C# body adds would have to reach Swift for this to mean anything.
    ///
    /// Nothing can carry it: both closure ABIs marshal the argument by value, and C#'s
    /// `Action`/`Func` cannot express `ref` at all, so an emitted binding would compile on both
    /// sides and silently discard every mutation. The member is therefore refused and emitted as a
    /// tombstone — this fixture pins that refusal on the exact reported shape (a mutable options
    /// dictionary handed to a configuration block), and is where a future writeback implementation
    /// would first turn green.
    public func build(_ mutate: (inout [String: Int32]) -> Void) -> [String: Int32] {
        var options: [String: Int32] = ["seed": 1]
        mutate(&options)
        return options
    }
}
