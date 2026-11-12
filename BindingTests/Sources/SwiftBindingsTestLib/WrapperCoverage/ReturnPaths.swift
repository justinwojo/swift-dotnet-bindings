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

// MARK: - Closure Return with metadata-remapped by-value struct arg

/// Class returning a closure that takes a `String` argument — exercises the
/// invoke-thunk arg-marshalling path for a frozen struct whose C# projection is
/// metadata-less (`Swift.String` → `string`). The thunk must convert the
/// incoming `string` to `Swift.SwiftString` and marshal it through a retaining
/// value-witness copy rather than calling `GetTypeMetadataOrThrow<string>()`,
/// which throws at runtime.
public class StringArgTransformFactory {
    public let factor: Int32

    public init(factor: Int32) {
        self.factor = factor
    }

    /// Returns an escaping closure taking a `String`. The closure returns the
    /// UTF-8 length of the string multiplied by the stored factor, so the C#
    /// test can assert a value round-trip across the boundary.
    public func makeStringLength() -> (String) -> Int32 {
        let f = factor
        return { s in Int32(s.utf8.count) * f }
    }
}

/// Class returning a closure that takes a `Foundation.Data` argument — exercises
/// the same invoke-thunk arg-marshalling path for `Foundation.Data` → `byte[]`
/// (also metadata-less). The thunk must convert via
/// `Swift.Foundation.Data.FromByteArray(byte[])`, not `GetTypeMetadataOrThrow<byte[]>()`.
public class DataArgTransformFactory {
    public let factor: Int32

    public init(factor: Int32) {
        self.factor = factor
    }

    /// Returns an escaping closure taking `Data`. The closure returns the byte
    /// count multiplied by the stored factor.
    public func makeByteCount() -> (Data) -> Int32 {
        let f = factor
        return { d in Int32(d.count) * f }
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

// MARK: - DynamicSelf Return on Class, Swift-convention routes

/// Non-final class whose `Self`-returning members spread over the binding's call routes. The
/// members taking a string-set closure have no C-convention wrapper, so the binding calls them with
/// the Swift calling convention: the library entry point for the full forms, and a generated
/// `@_silgen_name` shim for the trimmed default-argument overload. The members taking a URL
/// request closure, an integer closure or only scalars go through C-convention wrappers; the
/// request closures also check that a `URLRequest` value reaches the managed handler intact.
///
/// A dynamic `Self` on a class is one class reference, returned directly in the first return
/// register. Every member returns `self` (or a fresh tracked instance for the factories), so a
/// caller that reads the result from an indirect buffer the callee never writes gets the wrong
/// object, and the +1 reference the callee really returned is leaked. Each instance feeds the
/// shared allocation counters so a leak probe sees that reference.
open class SelfChainingBuilder {
    public let tag: Int32
    public private(set) var registrations: Int32 = 0
    public private(set) var lastOffset: Int32 = -1
    public private(set) var lastQueueLabel: String = ""
    private var handlers: [(Int32) -> Void] = []
    private var requestHandlers: [(URLRequest) -> Void] = []
    private var tagSetHandlers: [(Set<String>) -> Void] = []
    private let trackedSerial: Int64

    public required init(tag: Int32) {
        self.tag = tag
        self.trackedSerial = recordTrackedAllocation(category: "SelfChainingBuilder", tag: tag)
    }

    deinit {
        recordTrackedDeallocation(serial: trackedSerial)
    }

    /// A leading defaulted parameter before an escaping closure: the binding gets a trimmed
    /// overload that drops the default and calls a generated Swift shim.
    @discardableResult
    open func onEvent(offset: Int32 = 0, perform handler: @escaping (Int32) -> Void) -> Self {
        registrations += 1
        lastOffset = offset
        handlers.append(handler)
        return self
    }

    /// The same shape with a dispatch queue default and a URL request closure.
    @discardableResult
    open func onRequestCreation(on queue: DispatchQueue = .main,
                                perform handler: @escaping (URLRequest) -> Void) -> Self {
        registrations += 1
        lastQueueLabel = queue.label
        requestHandlers.append(handler)
        return self
    }

    /// The same shape with a string-set closure, which has no C-convention wrapper.
    @discardableResult
    open func onTagSet(on queue: DispatchQueue = .main,
                       perform handler: @escaping (Set<String>) -> Void) -> Self {
        registrations += 1
        lastQueueLabel = queue.label
        tagSetHandlers.append(handler)
        return self
    }

    /// An escaping closure and no defaults.
    @discardableResult
    open func withHandler(_ handler: @escaping (Int32) -> Void) -> Self {
        registrations += 1
        handlers.append(handler)
        return self
    }

    /// A defaulted scalar parameter and no closure.
    @discardableResult
    open func touch(_ amount: Int32, offset: Int32 = 0) -> Self {
        registrations += amount
        lastOffset = offset
        return self
    }

    /// `Self?`: one nullable class reference.
    open func selfIfTagged(_ wanted: Int32) -> Self? {
        return wanted == tag ? self : nil
    }

    /// `Self?` with a URL request closure.
    open func onRequestIfTagged(_ wanted: Int32, perform handler: @escaping (URLRequest) -> Void) -> Self? {
        guard wanted == tag else { return nil }
        registrations += 1
        requestHandlers.append(handler)
        return self
    }

    /// `Self?` with a string-set closure.
    open func onTagSetIfTagged(_ wanted: Int32, perform handler: @escaping (Set<String>) -> Void) -> Self? {
        guard wanted == tag else { return nil }
        registrations += 1
        tagSetHandlers.append(handler)
        return self
    }

    /// A class factory returning `Self`, taking a string-set closure.
    open class func makeForTagSets(tag: Int32, perform handler: @escaping (Set<String>) -> Void) -> Self {
        let instance = self.init(tag: tag)
        instance.onTagSet(perform: handler)
        return instance
    }

    /// A class factory returning `Self`, taking a URL request closure.
    open class func makeForRequests(tag: Int32, perform handler: @escaping (URLRequest) -> Void) -> Self {
        let instance = self.init(tag: tag)
        instance.onRequestCreation(perform: handler)
        return instance
    }

    /// A class factory returning `Self`.
    open class func make(tag: Int32, offset: Int32 = 0, perform handler: @escaping (Int32) -> Void) -> Self {
        let instance = self.init(tag: tag)
        instance.onEvent(offset: offset, perform: handler)
        return instance
    }

    /// An operator returning `Self`: a fresh instance whose tag is the sum of both tags.
    public static func + (lhs: SelfChainingBuilder, rhs: SelfChainingBuilder) -> Self {
        return self.init(tag: lhs.tag &+ rhs.tag)
    }

    /// An operator returning `Self?`: a fresh instance whose tag is the difference of the tags, or
    /// nil when that difference is negative.
    public static func - (lhs: SelfChainingBuilder, rhs: SelfChainingBuilder) -> Self? {
        let difference = lhs.tag &- rhs.tag
        return difference >= 0 ? self.init(tag: difference) : nil
    }

    /// An operator returning the class itself as an Optional: a fresh instance whose tag is the
    /// product of the tags, or nil when that product is zero.
    public static func * (lhs: SelfChainingBuilder, rhs: SelfChainingBuilder) -> SelfChainingBuilder? {
        let product = lhs.tag &* rhs.tag
        return product != 0 ? SelfChainingBuilder(tag: product) : nil
    }

    /// Calls every registered integer handler with `value + tag`, returning how many ran.
    public func fire(_ value: Int32) -> Int32 {
        for handler in handlers { handler(value + tag) }
        return Int32(handlers.count)
    }

    /// Calls every registered request handler with a request for `url`, returning how many ran.
    public func fireRequests(_ url: String) -> Int32 {
        let request = URLRequest(url: URL(string: url)!)
        for handler in requestHandlers { handler(request) }
        return Int32(requestHandlers.count)
    }

    /// Calls every registered string-set handler with the set `[first, second]`, returning how many ran.
    public func fireTagSets(_ first: String, _ second: String) -> Int32 {
        let tags: Set<String> = [first, second]
        for handler in tagSetHandlers { handler(tags) }
        return Int32(tagSetHandlers.count)
    }

    /// Swift object identity, so a caller can tell whether a returned reference is this object.
    public func isSameObject(as other: SelfChainingBuilder) -> Bool {
        return self === other
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

// MARK: - Void-Parameter Method with Array Return

/// Struct with a static method whose only parameter is `Void` (empty tuple) and
/// which returns an Array — modeled on Swift result-builder overloads such as
/// `buildPartialBlock(first: Void) -> [T]` (TipKit's `Tips.GroupBuilder`).
///
/// Regression guard for two interacting code paths:
///   1. The `[Int32]` return is a frozen 8-byte (single-pointer) value that
///      declines TypeLowering, so the method routes through the @_cdecl wrapper
///      rather than a native thunk.
///   2. On that wrapper path, the `Void` parameter contributes no @_cdecl ABI
///      parameter, yet Swift still requires the argument at the call site.
///      `CdeclSignatureContract.HasArguments` must keep the Arguments phase so
///      the wrapper emits `make(first: ())` — emitting `make()` fails to compile
///      ("missing argument for parameter 'first'").
public struct VoidParamArrayFactory {
    /// Single Void parameter, Array return. The values let the C# test assert a
    /// round-trip rather than just a non-crash.
    public static func make(first: Void) -> [Int32] {
        return [10, 20, 30]
    }
}
