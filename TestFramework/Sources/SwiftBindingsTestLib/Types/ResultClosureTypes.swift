// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

/// Test types for Result<T,E> closure bridge with non-frozen struct params.
/// Exercises MethodClosureBridge with:
/// - Non-frozen struct as non-closure param (PayloadHandle category)
/// - Result<Class, NonSimpleEnum> as closure arg (bound generic)

public enum FetchError: Error {
    case networkError(code: Int)
    case timeout
    case invalidResponse(message: String)
}

public class FetchResult {
    public var data: String
    public init(data: String) { self.data = data }
}

public class ResultClosureTest {
    public init() {}

    /// Method with non-frozen struct param + Result<Class, NonSimpleEnum> closure.
    /// Tests the MethodClosureBridge path: ImageRequest-like non-frozen struct
    /// passes as PayloadHandle, Result passes as bound generic via withUnsafePointer.
    public func fetchData(
        request: NonFrozenPoint,
        completion: @escaping (Result<FetchResult, FetchError>) -> Void
    ) {
        completion(.success(FetchResult(data: "ok")))
    }

    /// Static variant — verifies static method emission path.
    public static func staticFetch(
        request: NonFrozenPoint,
        completion: @escaping (Result<FetchResult, FetchError>) -> Void
    ) {
        completion(.success(FetchResult(data: "static-ok")))
    }
}
