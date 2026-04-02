// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

/// Test types for Result<T,E> return value marshalling.
/// Exercises the new @_cdecl wrapper generation for Result types
/// transported via UnsafeRawPointer (indirect result).

/// Simple error enum for Result tests.
public enum ResultTestError: Error, Equatable {
    case notFound
    case invalidInput(message: String)
}

/// Class for testing Result<Class, Error>.
public class ResultPayload {
    public var value: String
    public init(value: String) { self.value = value }
}

/// Class with methods returning various Result types.
public class ResultReturnTest {
    public init() {}

    /// Returns a successful Result<Int32, ResultTestError>.
    public func getSuccessInt() -> Result<Int32, ResultTestError> {
        return .success(42)
    }

    /// Returns a failure Result<Int32, ResultTestError>.
    public func getFailureInt() -> Result<Int32, ResultTestError> {
        return .failure(.notFound)
    }

    /// Returns a successful Result with a class payload.
    public func getSuccessPayload() -> Result<ResultPayload, ResultTestError> {
        return .success(ResultPayload(value: "hello"))
    }

    /// Returns a failure Result with a class payload.
    public func getFailurePayload() -> Result<ResultPayload, ResultTestError> {
        return .failure(.invalidInput(message: "bad input"))
    }

    /// Static method returning Result.
    public static func staticSuccess() -> Result<Int32, ResultTestError> {
        return .success(99)
    }

    /// Property returning Result.
    public var currentResult: Result<Int32, ResultTestError> {
        return .success(7)
    }
}
