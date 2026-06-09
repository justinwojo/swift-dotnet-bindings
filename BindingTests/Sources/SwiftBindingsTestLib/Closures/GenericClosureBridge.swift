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

    // Synthetic-name collision guard: the GenericClosureBridge @_cdecl wrapper hardcodes synthetic
    // Swift locals — `cdecl` (the `unsafeBitCast` func-ptr local) and `_self`/`__self` (the self
    // pointer param and its reconstruction local). A user non-closure param spelled the same name
    // collides with the wrapper local and produces an "invalid redeclaration" at swiftc time
    // (generator already exited 0). The guard reserves every synthetic through a
    // `SyntheticNameScope` seeded with the user param names, renaming a colliding synthetic to a
    // `__`-prefixed variant.

    /// User param `cdecl` collides with the synthetic func-ptr local. Routes through the
    /// GenericClosureBridge (method-generic, noescape, throwing closure with a generic return).
    public func readWithCdecl<T>(cdecl: DatabaseReader, _ block: (DatabaseReader) throws -> T) rethrows -> T {
        return try block(cdecl)
    }

    /// User param `_self` collides with the synthetic self-pointer param name — the guard must
    /// rename it transitively (`_self` is taken by the user, so the synthetic falls through to
    /// `__self`, which is also reserved, to `___self`).
    public func readWithSelf<T>(_self: DatabaseReader, _ block: (DatabaseReader) throws -> T) rethrows -> T {
        return try block(_self)
    }

    /// Invokes the closure — which writes its `+1` result into the GenericClosureBridge resultBuf —
    /// and then throws on its own. This is the throw-AFTER-callback exception path: unlike `read` /
    /// `readWithCdecl` / `readWithSelf` (which `rethrows`, so they only throw when the closure itself
    /// threw and therefore never wrote a result), this `throws` independently after a SUCCESSFUL
    /// closure invocation, so the bridge's resultBuf holds an unconsumed `+1` when the C# side takes
    /// the Swift-error path. The generated returning bridge must value-witness Destroy that buffer on
    /// the error path or the closure result leaks once per call. Drives the GCB leak probe.
    public func readThenThrow<T>(from source: DatabaseReader, _ block: (DatabaseReader) throws -> T) throws -> T {
        _ = try block(source)
        throw DatabaseReadError.afterRead
    }
}

/// Error thrown by `DatabaseReader.readThenThrow` after a successful closure invocation, exercising
/// the GenericClosureBridge throw-after-callback exception cleanup path.
public enum DatabaseReadError: Error {
    case afterRead
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

// MARK: - Non-escaping MCB closure (ClosureHandle non-escaping policy regression)

/// Instance method on a non-generic class with a non-escaping closure whose argument
/// is a complex enum — MCB-eligible by virtue of the `ProcessResult` arg. Pre-
/// `ClosureHandle` the MCB emit path allocated a `GCHandle` to root the C# delegate
/// but its try/finally was gated on `anyEscaping`, so the non-escaping branch never
/// freed the handle and the captured delegate (plus everything it referenced) leaked
/// for the process lifetime. The closure-handle helper unconditionally disposes the
/// handle in finally; the `NonEscaping` policy always frees on dispose.
///
/// Must be a class method (not a free function) so MCB activates — the regular
/// WrapperEmitter path already frees non-escaping handles correctly and would mask
/// the MCB-specific regression.
public final class NonEscapingMCBFixture {
    public init() {}

    /// Closure return type is `Bool` (one of MCB's two accepted closure return
    /// types — Void or Bool); the argument is the complex enum `ProcessResult`.
    /// The closure is non-`@escaping`, so MCB takes the path that previously
    /// leaked the `GCHandle`.
    public func runSynchronously(_ predicate: (ProcessResult) -> Bool) -> Bool {
        return predicate(.success(value: 5))
    }
}
