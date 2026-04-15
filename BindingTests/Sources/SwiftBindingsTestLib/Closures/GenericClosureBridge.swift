// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - MCB on Generic Parent (MethodClosureBridge extension approach)

/// Complex enum for triggering MethodClosureBridge path (same as TransformOutcome).
/// Bound generic closure args + generic parent = tests the @_silgen_name extension approach.
public enum ProcessResult {
    case success(value: Int32)
    case failure(code: Int32)
}

/// Inspects a ProcessResult and returns the associated Int32 value.
public func processResultValue(_ pr: ProcessResult) -> Int32 {
    switch pr {
    case .success(let v): return v
    case .failure(let c): return c
    }
}

/// Whether a ProcessResult is the success case.
public func processResultIsSuccess(_ pr: ProcessResult) -> Bool {
    if case .success = pr { return true }
    return false
}

/// Generic class with a closure-bearing instance method.
/// Tests MCB on generic parents: the generated Swift wrapper must use @_silgen_name
/// extension (not @_cdecl free function) to inherit the generic context.
public class GenericProcessor<T> {
    public let label: String
    public let initialValue: T

    public init(label: String, initialValue: T) {
        self.label = label
        self.initialValue = initialValue
    }

    /// Instance method with closure taking a complex enum arg — triggers MCB path.
    /// Parent is generic (GenericProcessor<T>) so requires extension approach.
    public func run(completion: @escaping (ProcessResult) -> Void) {
        completion(.success(value: 42))
    }

    /// Instance method with closure taking a complex enum arg and returning Bool.
    public func runWithFilter(predicate: @escaping (ProcessResult) -> Bool) -> Bool {
        return predicate(.success(value: 100))
    }
}

// MARK: - GenericClosureBridge with non-closure params

/// Class with generic closure method that also has non-closure parameters.
/// Tests GenericClosureBridge non-closure param support.
public class DatabaseReader {
    public let name: String

    public init(name: String) {
        self.name = name
    }

    /// Method-level generic closure with a non-closure class parameter.
    /// The GenericClosureBridge must pass the extra parameter through the P/Invoke.
    public func read<T>(from source: DatabaseReader, _ block: (DatabaseReader) throws -> T) rethrows -> T {
        return try block(source)
    }
}

// MARK: - Swift.String as MCB non-closure param (Stripe pattern)

/// Fixture exercising a `Swift.String` non-closure parameter on an MCB-eligible
/// method. MCB activates via the `Result<T, any Error>` closure arg; the String
/// must be passed as a UTF-8 (pointer, length) pair via the Utf8Slice category
/// so the C# wrapper can pin the bytes with `fixed` rather than allocating a
/// SwiftString payload buffer.
public final class StringParamMCBFixture {
    public init() {}

    /// Returns the length of the input string as a Result wrapped in the
    /// completion callback. The name mirrors Stripe's `possibleBrands(forNumber:)`
    /// signature — single String non-closure param + Result closure.
    public func measure(input: String, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(Int32(input.utf8.count)))
    }
}

// MARK: - Optional MCB closure (Nuke / GRDB / Kingfisher pattern)

/// Non-generic class exposing an Optional closure whose argument is `any Error` —
/// MCB-eligible because of the existential. Models the Kingfisher / Nuke / GRDB
/// completion-handler pattern where the closure may be nil. MCB must:
///   * NOT force-unwrap the funcPtr (passing nil must round-trip as nil),
///   * generate a nullable C# delegate parameter,
///   * skip the GCHandle.Alloc when the delegate is null.
public final class OptionalErrorCallbackFixture {
    public init() {}

    /// Optional `(any Error) -> Void` closure. Returns the count of times the
    /// wrapper invoked it: 1 when a non-nil callback was passed, 0 when nil.
    public func reportIfPresent(callback: ((any Error) -> Void)?) -> Int32 {
        if let callback = callback {
            callback(MathError.divisionByZero)
            return 1
        }
        return 0
    }
}
