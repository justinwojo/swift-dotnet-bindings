// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

/// Two classes with same-named closure methods to exercise MCB function name dedup.
/// Before the fix, both `process(completion:)` methods would emit `_sbw_mcb_process`
/// in the Swift wrapper, causing a redeclaration error. After the fix, each gets
/// a unique name incorporating the mangled hash: `_sbw_mcb_MCB_{hash}_process`.

public class DataProcessor {
    public var name: String

    public init(name: String) {
        self.name = name
    }

    /// Closure with non-frozen struct arg (bound generic path in MCB).
    public func process(completion: @escaping (Result<FetchResult, FetchError>) -> Void) {
        completion(.success(FetchResult(data: "processed-by-\(name)")))
    }

    /// Returns a failure result — exercises SwiftResult.TryGetFailure path.
    public func processWithError(completion: @escaping (Result<FetchResult, FetchError>) -> Void) {
        completion(.failure(.networkError(code: 404)))
    }
}

public class ImageProcessor {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Same method name as DataProcessor.process — exercises MCB dedup.
    public func process(completion: @escaping (Result<FetchResult, FetchError>) -> Void) {
        completion(.success(FetchResult(data: "image-\(label)")))
    }
}
