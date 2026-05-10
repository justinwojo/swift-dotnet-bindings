// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Typed Throws (Swift 6.0)
// Tests: Typed throws with specific error types in function signatures
// Expected C#: Error type encoded in ABI; may require typed error marshalling
// Limitation: Typed throws are not yet supported by the generator

/// Error type for testing typed throws — parse failures.
public enum ParseError: Error {
    /// The input string could not be parsed.
    case invalidInput
    /// The parsed value exceeded the representable range.
    case overflow(value: String)
}

/// Error type for testing multiple typed-error types in the same module.
public enum RangeError: Error {
    /// The value was below the minimum.
    case belowMinimum(value: Int32, minimum: Int32)
    /// The value was above the maximum.
    case aboveMaximum(value: Int32, maximum: Int32)
}

// MARK: - Free Functions with Typed Throws

/// Parses a string into an Int32, throwing a typed ParseError on failure.
public func parseNumber(_ input: String) throws(ParseError) -> Int32 {
    guard let value = Int32(input) else {
        throw ParseError.invalidInput
    }
    return value
}

/// Validates that a value is within a range, throwing a typed RangeError.
public func validateRange(_ value: Int32, min: Int32, max: Int32) throws(RangeError) -> Int32 {
    if value < min {
        throw RangeError.belowMinimum(value: value, minimum: min)
    }
    if value > max {
        throw RangeError.aboveMaximum(value: value, maximum: max)
    }
    return value
}

// MARK: - Async Typed Throws

/// Async free function with typed throws — simulates async parsing.
public func asyncParse(_ input: String) async throws(ParseError) -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    guard let value = Int32(input) else {
        throw ParseError.invalidInput
    }
    return value
}

// MARK: - Struct with Typed Throwing Method

/// A parser struct with typed throwing instance methods.
public struct TypedThrowingParser {
    public let strict: Bool

    public init(strict: Bool) {
        self.strict = strict
    }

    /// Parses input with typed throws.
    public func parse(_ input: String) throws(ParseError) -> Int32 {
        guard !input.isEmpty else {
            throw ParseError.invalidInput
        }
        if strict {
            // In strict mode, reject leading/trailing whitespace
            let trimmed = input.trimmingCharacters(in: .whitespaces)
            guard trimmed == input else {
                throw ParseError.invalidInput
            }
        }
        guard let value = Int32(input) else {
            if input.count > 10 {
                throw ParseError.overflow(value: input)
            }
            throw ParseError.invalidInput
        }
        return value
    }
}

// MARK: - Async Typed Throws (Instance Method)
// Instance methods on types work with async typed throws (free functions have a known bug).

extension TypedThrowingParser {
    /// Async parse with typed throws — simulates async parsing.
    public func asyncParse(_ input: String) async throws(ParseError) -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return try parse(input)
    }
}

// MARK: - Free Functions (Creation Helpers)

/// Creates a strict TypedThrowingParser.
public func createStrictParser() -> TypedThrowingParser {
    return TypedThrowingParser(strict: true)
}

/// Creates a lenient TypedThrowingParser.
public func createLenientParser() -> TypedThrowingParser {
    return TypedThrowingParser(strict: false)
}

// MARK: - Async Typed Throws — Class-shaped error (Fix G parity)

/// Class-shaped error type for typed-throws. Mirrors the cascade dispatcher's
/// `ClassPointerDirect` shape: the wire is a +1 retained class pointer with no
/// carrier buffer. Distinct from `PlainThrowsScanError` in `ErrorTypes.swift`
/// (which exercises the same shape via the *plain*-throws cascade) so the typed
/// path can assert it independently of the cascade path.
public class TypedThrowsScanError: Error {
    public let code: Int32
    public let detail: String

    public init(code: Int32, detail: String) {
        self.code = code
        self.detail = detail
    }
}

/// Async function with typed throws of a class-shaped error. Exercises the
/// `typedErrorIsClassDirectAsync` branch of the async typed-throws emitter:
/// Swift hands a +1 retained class pointer via `Unmanaged.passRetained(... as
/// AnyObject).toOpaque()`, and C# `MarshalFromSwift<T>` constructs a SwiftObject
/// taking ownership of the retain. There is nothing to `SBW_Free` for this shape.
public func asyncScanTyped(_ input: String) async throws(TypedThrowsScanError) -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if input.isEmpty {
        throw TypedThrowsScanError(code: 404, detail: "empty input rejected")
    }
    if input == "denied" {
        throw TypedThrowsScanError(code: 403, detail: "scanning denied for: \(input)")
    }
    return Int32(input.count)
}
