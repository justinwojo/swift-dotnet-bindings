// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sync plain-throws → SwiftException<TError> cascade fixtures
//
// The async plain-throws fixtures (`plainThrowsAsync*` in ThrowingFunctions.swift)
// prove that a plain `async throws` whose thrown error is a registered
// Error-conforming type of this module surfaces as `SwiftException<TError>` with a
// real payload. These fixtures are the synchronous twins: every cascade payload
// shape — value-copy (simple enum), buffer-owned-by-SafeHandle (complex enum and
// non-frozen struct), class-pointer-direct, and the copied buffer that needs a
// value-witness destroy before it is freed (a frozen struct with a heap-typed
// field) — thrown from plain `throws` members, plus an unregistered error that must
// keep the untyped fallback, plus two members whose errors carry a
// `LifetimeTracker`-counted ref so a leaked or double-released error box is
// observable rather than merely "did not crash".
//
// Member shapes are deliberately varied so the emitted bindings cover every route a
// sync `throws` member can take for the same error mechanism: the native thunk and the
// `@_cdecl` wrapper (both reporting through an explicit error out-pointer), and the
// direct call reporting through the dedicated Swift error register — the last reached
// by a `get throws` computed property, which the property wrapper declines.

// MARK: Registered error types (one per cascade payload shape)

/// Simple enum error — the value-copy cascade shape. `MarshalFromSwift` reads the
/// matched value out of the wire buffer by value.
public enum SyncCascadeSimpleError: Error {
    case unused
    case divisionByZero
    case negativeInput
}

/// Complex enum error (cases with associated values) — the
/// buffer-owned-by-SafeHandle cascade shape. The tests assert against a case that
/// is NOT the first declared one, so a "default discriminant happened to match"
/// false positive cannot pass.
public enum SyncCascadeComplexError: Error {
    case unused
    case malformed(reason: String)
    case unexpectedEOF(at: Int32)
}

/// Non-frozen struct error — the other buffer-owned-by-SafeHandle shape.
public struct SyncCascadeStructError: Error {
    public let path: String
    public let lineNumber: Int32

    public init(path: String, lineNumber: Int32) {
        self.path = path
        self.lineNumber = lineNumber
    }
}

/// Class error — the class-pointer-direct cascade shape. Swift hands a `+1`
/// retained class pointer over the wire (no carrier buffer).
public class SyncCascadeClassError: Error {
    public let code: Int32
    public let detail: String

    public init(code: Int32, detail: String) {
        self.code = code
        self.detail = detail
    }
}

// MARK: Lifetime-instrumented error types (release-exactly-once probes)

/// Class error embedding a `LifetimeTracker`-counted ref (LeakDetection.swift).
/// Every throw allocates one tracked object; after the caller drops the caught
/// exception and the finalizers drain, the live count must return to its baseline.
/// A never-released Swift error box (or a never-released class-direct payload)
/// pins the ref and leaves the count non-zero; a double release trips the Swift
/// runtime instead of merely miscounting.
public final class SyncCascadeTrackedClassError: Error {
    public let code: Int32
    private let ref: TrackedRef

    public init(code: Int32) {
        self.code = code
        self.ref = TrackedRef(tag: code, category: "SyncCascadeTrackedClassError")
    }
}

/// Non-frozen struct error embedding a `LifetimeTracker`-counted ref. The wire
/// carrier holds a value-witness `+1` on the embedded ref; the SafeHandle that
/// takes the carrier must release it when it finalizes.
public struct SyncCascadeTrackedStructError: Error {
    public let code: Int32
    private let ref: TrackedRef

    public init(code: Int32) {
        self.code = code
        self.ref = TrackedRef(tag: code, category: "SyncCascadeTrackedStructError")
    }
}

// MARK: Throwing members — blittable free function

/// Plain `throws` free function with only blittable parameters and return.
public func syncCascadeDivide(a: Int32, b: Int32) throws -> Int32 {
    if a < 0 { throw SyncCascadeSimpleError.negativeInput }
    guard b != 0 else { throw SyncCascadeSimpleError.divisionByZero }
    return a / b
}

/// Plain `throws` free function taking a String — forces the wrapper route that
/// reports the error through an explicit out-pointer rather than the error register.
public func syncCascadeParse(_ input: String) throws -> Int32 {
    if input.isEmpty { throw SyncCascadeComplexError.unexpectedEOF(at: 42) }
    if input.first == "!" {
        throw SyncCascadeComplexError.malformed(reason: "leading punctuation: \(input)")
    }
    return Int32(input.count)
}

/// Plain `throws` free function whose error is a non-frozen struct.
public func syncCascadeLoadConfig(path: String) throws -> Int32 {
    if path.isEmpty { throw SyncCascadeStructError(path: "<empty>", lineNumber: 0) }
    if path == "/etc/bad" { throw SyncCascadeStructError(path: path, lineNumber: 7) }
    return Int32(path.count)
}

/// Plain `throws` free function whose error is a class.
public func syncCascadeScan(input: String) throws -> Int32 {
    if input.isEmpty { throw SyncCascadeClassError(code: 404, detail: "empty input rejected") }
    if input == "denied" { throw SyncCascadeClassError(code: 403, detail: "scanning denied") }
    return Int32(input.count)
}

/// Plain `throws` free function whose error is a frozen struct with a heap-typed
/// field, reusing the error type the asynchronous twin throws. That shape is
/// projected as a class over a copied carrier buffer, so the wire buffer walks away
/// holding value-witness retains on the heap field; the dispatch has to destroy the
/// buffer's retains before freeing it. Asserting on both marshalled fields shows the
/// copy reached the caller intact rather than being read out of a buffer that was
/// freed without the destroy.
public func syncCascadeOpenResource(named name: String) throws -> Int32 {
    if name.isEmpty { throw PlainThrowsFrozenWithMemoryError(resourceName: "config.plist", attempts: 7) }
    if name == "denied" { throw PlainThrowsFrozenWithMemoryError(resourceName: "secrets.json", attempts: 13) }
    return Int32(name.count)
}

/// Plain `throws` free function that throws an Error from OUTSIDE this module's
/// registry (Foundation `NSError`). No cascade arm matches, so the caller must keep
/// receiving the untyped `SwiftException` carrying only the description.
public func syncCascadeThrowUnregistered() throws -> Int32 {
    throw NSError(
        domain: "SwiftBindingsTestLib.SyncCascadeUnregisteredDomain",
        code: 8888,
        userInfo: [NSLocalizedDescriptionKey: "sync-fallthrough-sentinel-8888"])
}

// MARK: Throwing members — instance methods

/// Struct with plain `throws` instance methods across the same payload shapes.
public struct SyncCascadeWorker {
    public let seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    /// Blittable instance method.
    public func divide(_ divisor: Int32) throws -> Int32 {
        guard divisor != 0 else { throw SyncCascadeSimpleError.divisionByZero }
        return seed / divisor
    }

    /// String-taking instance method whose error is a complex enum.
    public func parse(_ input: String) throws -> Int32 {
        if input.isEmpty { throw SyncCascadeComplexError.unexpectedEOF(at: seed) }
        return Int32(input.count) + seed
    }
}

/// Class with plain `throws` instance methods.
public final class SyncCascadeService {
    public init() {}

    public func load(key: String) throws -> Int32 {
        if key.isEmpty { throw SyncCascadeClassError(code: 404, detail: "missing key") }
        return Int32(key.count)
    }
}

/// Struct with a synchronous `get throws` computed property. The `@_cdecl` property wrapper
/// declines a throwing getter, so this member is emitted as an ordinary direct call whose thrown
/// error arrives in the dedicated Swift error register rather than through an explicit
/// out-pointer — the third route a sync `throws` member can take.
public struct SyncCascadeGate {
    public let allow: Bool

    public init(allow: Bool) {
        self.allow = allow
    }

    public var checkedValue: Int32 {
        get throws {
            if !allow { throw SyncCascadeSimpleError.negativeInput }
            return 7
        }
    }
}

// MARK: Throwing members — lifetime probes

/// Throws a class error embedding a tracked ref. Always throws.
public func syncCascadeThrowTrackedClassError(code: Int32) throws -> Int32 {
    throw SyncCascadeTrackedClassError(code: code)
}

/// Throws a non-frozen struct error embedding a tracked ref. Always throws.
public func syncCascadeThrowTrackedStructError(code: Int32) throws -> Int32 {
    throw SyncCascadeTrackedStructError(code: code)
}
