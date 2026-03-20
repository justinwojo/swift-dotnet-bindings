// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Tuple Return

/// Class with method returning tuple — exercises
/// WrapperEmitter.Return:279-291 (@_cdecl tuple return) and
/// PInvokeEmitter:100-128 (tuple return type).
public class PairMaker {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Returns a tuple of (Int32, String).
    public func makePair(value: Int32) -> (Int32, String) {
        return (value, "\(label):\(value)")
    }
}

// MARK: - Closure Return

/// Class with method returning closure — exercises
/// WrapperEmitter.Return:250-269 (@_cdecl closure return) and
/// PInvokeEmitter:73-92 (closure return type).
public class TransformFactory {
    public let multiplier: Int32

    public init(multiplier: Int32) {
        self.multiplier = multiplier
    }

    /// Returns an escaping closure.
    public func makeTransform() -> (Int32) -> Int32 {
        let m = multiplier
        return { x in x * m }
    }
}

// MARK: - Optional<Closure> Return

/// Class with method returning Optional closure — exercises
/// WrapperEmitter.Return:151-175 (@_cdecl + Optional<closure> return).
/// NOTE: Using Int32 return instead of String inside closure because
/// Optional<(Int32) -> String> generates invalid C# (void* → string cast bug).
/// The String variant is tracked as a finding from this audit.
public class OptionalHandlerFactory {
    public let enabled: Bool

    public init(enabled: Bool) {
        self.enabled = enabled
    }

    /// Returns an optional closure — nil when disabled.
    public func makeHandler() -> ((Int32) -> Int32)? {
        guard enabled else { return nil }
        return { value in value * 2 }
    }
}

// MARK: - DynamicSelf Return on Class

/// Non-final class with method returning Self — exercises
/// PInvokeEmitter:179-182 (DynamicSelf + @_cdecl).
/// Real-world pattern: builder methods returning Self.
open class Buildable {
    public var tag: Int32

    public required init(tag: Int32) {
        self.tag = tag
    }

    /// Returns Self — must dispatch dynamically.
    open func withTag(_ newTag: Int32) -> Self {
        let instance = type(of: self).init(tag: newTag)
        return instance
    }

    public func describe() -> String {
        return "Buildable(tag:\(tag))"
    }
}

// MARK: - DynamicSelf Return on Struct (guard path)

/// Struct with method returning Self — exercises
/// MethodWrapperEmitter:145-146 (Self return on struct guard).
/// On structs, `Self` is just the struct type — should be simpler to emit.
public struct CopyableValue {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func withValue(_ newValue: Int32) -> CopyableValue {
        return CopyableValue(value: newValue)
    }

    public func describe() -> String {
        return "CopyableValue(\(value))"
    }
}

// MARK: - String Return via @_cdecl

/// Class with method returning String via @_cdecl wrapper — exercises
/// WrapperEmitter.Return:102-117 (Utf8Slice inline decode path).
public class Greeter {
    public let name: String

    public init(name: String) {
        self.name = name
    }

    /// String return through @_cdecl wrapper.
    public func greet(greeting: String) -> String {
        return "\(greeting), \(name)!"
    }
}
