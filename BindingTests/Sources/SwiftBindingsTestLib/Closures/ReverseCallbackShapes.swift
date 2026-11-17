// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse callbacks Swift invokes, by lane
//
// Each shape here hands a C# delegate to Swift as a closure and has Swift invoke it, once while the
// call that handed it over is still running and once after that call has returned. The values that
// cross are wide enough to leave registers (a non-frozen struct with a String, over 16 bytes), one
// callback throws, and the property setters carry their receiver in the self register.
//
// Two lanes carry them:
// - The `Result` completions beside a value with no C representation (`URL`) reach C# through the
//   `@_cdecl` closure adapter: Swift hands the typed success or failure to C# by address.
// - The rest have no `@_cdecl` adapter and stay on the Swift-convention lane: the callback is an
//   `[UnmanagedCallersOnly]` method with the Swift convention, which the interpreter on x86_64
//   cannot receive. The member that hands it over is guarded for that shape, so an engine that
//   refuses it throws before any native code runs, and the transition checks accept the callback
//   because that guarded member is the only way to reach it.

/// A non-frozen struct wider than the argument registers.
public struct ReverseCallbackWide: Equatable {
    public var first: Int
    public var second: Int
    public var third: Int
    public var label: String

    public init(first: Int, second: Int, third: Int, label: String) {
        self.first = first
        self.second = second
        self.third = third
        self.label = label
    }
}

/// An error with an associated value.
public enum ReverseCallbackFailure: Error, Equatable {
    case rejected(code: Int)
    case unavailable
}

/// A class result for a `Result` completion with an untyped error.
public final class ReverseCallbackNode {
    public let name: String
    public init(name: String) { self.name = name }
}

/// Encodes an integer; returned by a stored factory closure as an existential.
public protocol ReverseCallbackEncoding {
    func encode(_ value: Int) -> Int
}

/// The fixture's own encoding, so a test can tell a Swift-vended encoder from a C# one.
public struct ReverseCallbackOffsetEncoding: ReverseCallbackEncoding {
    public let offset: Int
    public init(offset: Int) { self.offset = offset }
    public func encode(_ value: Int) -> Int { value + offset }
}

// MARK: Result completions beside a URL (the @_cdecl adapter)

/// Loads by URL and reports through `Result` completions.
public final class ReverseCallbackLoader {
    private var pendingWide: [(URL, Int, (Result<ReverseCallbackWide, ReverseCallbackFailure>) -> Void)] = []
    private var pendingNodes: [(URL, (Result<ReverseCallbackNode, Error>) -> Void)] = []

    public init() {}

    /// Completes before returning: success when `code` is zero, otherwise `.rejected(code:)`.
    public func load(url: URL, code: Int, completion: (Result<ReverseCallbackWide, ReverseCallbackFailure>) -> Void) {
        completion(Self.outcome(url: url, code: code))
    }

    /// Holds the completion until `finish()`.
    public func loadLater(url: URL, code: Int, completion: @escaping (Result<ReverseCallbackWide, ReverseCallbackFailure>) -> Void) {
        pendingWide.append((url, code, completion))
    }

    /// Holds a class-result completion until `finish()`; an empty last path component fails.
    public func loadNodeLater(url: URL, completion: @escaping (Result<ReverseCallbackNode, Error>) -> Void) {
        pendingNodes.append((url, completion))
    }

    /// Completes every held load, in order, and returns how many completed.
    @discardableResult
    public func finish() -> Int {
        let wide = pendingWide
        let nodes = pendingNodes
        pendingWide = []
        pendingNodes = []
        for (url, code, completion) in wide {
            completion(Self.outcome(url: url, code: code))
        }
        for (url, completion) in nodes {
            let name = url.lastPathComponent
            completion(name.isEmpty || name == "/" ? .failure(ReverseCallbackFailure.unavailable) : .success(ReverseCallbackNode(name: name)))
        }
        return wide.count + nodes.count
    }

    private static func outcome(url: URL, code: Int) -> Result<ReverseCallbackWide, ReverseCallbackFailure> {
        code == 0
            ? .success(ReverseCallbackWide(first: 1, second: 2, third: 3, label: url.lastPathComponent))
            : .failure(.rejected(code: code))
    }
}

// MARK: An optional wide argument (the Swift-convention lane)

/// Relays an optional wide value to a callback.
public final class ReverseCallbackRelay {
    private var pending: [(ReverseCallbackWide?, (ReverseCallbackWide?) -> Void)] = []

    public init() {}

    /// Invokes `callback` with `value` before returning.
    public func deliver(_ value: ReverseCallbackWide?, to callback: @escaping (ReverseCallbackWide?) -> Void) {
        callback(value)
    }

    /// Holds `callback` until `flush()`.
    public func deliverLater(_ value: ReverseCallbackWide?, to callback: @escaping (ReverseCallbackWide?) -> Void) {
        pending.append((value, callback))
    }

    /// Invokes every held callback, in order, and returns how many ran.
    @discardableResult
    public func flush() -> Int {
        let held = pending
        pending = []
        for (value, callback) in held {
            callback(value)
        }
        return held.count
    }
}

// MARK: Stored closures on a value type (the Swift-convention lane, receiver in the self register)

/// A value type whose stored closures C# replaces and Swift later invokes.
public struct ReverseCallbackSettings {
    /// A plain integer transform.
    public var transform: (Int) -> Int
    /// A provider whose wide optional result Swift receives by address.
    public var provider: () -> ReverseCallbackWide?
    /// A factory returning an existential.
    public var makeEncoder: (Int) -> any ReverseCallbackEncoding
    /// A validator that throws.
    public var validate: (Int) throws -> Int

    public init() {
        transform = { $0 }
        provider = { nil }
        makeEncoder = { ReverseCallbackOffsetEncoding(offset: $0) }
        validate = { $0 }
    }

    /// `transform(value)`.
    public func apply(_ value: Int) -> Int { transform(value) }

    /// `provider()`.
    public func provide() -> ReverseCallbackWide? { provider() }

    /// `makeEncoder(offset).encode(value)`.
    public func encode(_ value: Int, offset: Int) -> Int { makeEncoder(offset).encode(value) }

    /// `validate(value)`, or `-1` when it throws a `ReverseCallbackFailure`, or `-2` for any other error.
    public func check(_ value: Int) -> Int {
        do {
            return try validate(value)
        } catch is ReverseCallbackFailure {
            return -1
        } catch {
            return -2
        }
    }
}
