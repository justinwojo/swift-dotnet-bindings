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

/// Enum with a Result<T,E> associated value.
/// Tests that the generator doesn't crash when encountering Result in parameter
/// direction (enum case construction). Previously caused a fatal
/// NotSupportedException in ResultProjection.GetParameterPlan.
public enum OperationOutcome {
    case completed(Result<String, ResultTestError>)
    case pending
    case cancelled(reason: String)
}

/// Helper to create OperationOutcome values from Swift (since enum case
/// construction with Result param may not be available from C#).
public class OperationOutcomeFactory {
    public init() {}

    public func makePending() -> OperationOutcome {
        return .pending
    }

    public func makeCancelled(reason: String) -> OperationOutcome {
        return .cancelled(reason: reason)
    }

    public func makeCompletedSuccess(value: String) -> OperationOutcome {
        return .completed(.success(value))
    }

    public func makeCompletedFailure() -> OperationOutcome {
        return .completed(.failure(.notFound))
    }
}
