// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foundation.URLError.Code across every call route

// `URLError.Code` holds one `Int`, and the binding projects it as `nint`. The SDK does not
// declare it `@frozen`, though, and Foundation is built with library evolution, so to every
// client it is address-only: a parameter arrives `@in_guaranteed` (a pointer to the value) and a
// result leaves through an indirect-result buffer. A Swift-convention P/Invoke that passes the
// `nint` in a register therefore hands Swift an integer it reads as an address, and one that
// expects the result in a register reads whatever was left there.
//
// Every shape below must reach Swift through a route that carries the value in memory or as a
// plain `Int` that a wrapper rebuilds. Each raw value is unusual so a value read from the wrong
// place cannot pass by accident.

public struct UrlErrorCodeHolder {
    public var code: URLError.Code

    public init(code: URLError.Code) { self.code = code }

    public func echo(_ value: URLError.Code) -> URLError.Code { value }

    public func rawValue(of value: URLError.Code) -> Int { value.rawValue }

    public func combined(_ first: URLError.Code, _ second: URLError.Code) -> Int {
        first.rawValue &* 1000 &+ second.rawValue
    }

    public subscript(value: URLError.Code) -> Int { value.rawValue &+ code.rawValue }

    public func echoLater(_ value: URLError.Code) async -> URLError.Code { value }
}

public final class UrlErrorCodeBox {
    public var code: URLError.Code

    public init(code: URLError.Code) { self.code = code }

    public func echo(_ value: URLError.Code) -> URLError.Code { value }

    public func storedRawValue() -> Int { code.rawValue }

    public static func make(rawValue: Int) -> URLError.Code { URLError.Code(rawValue: rawValue) }
}

public struct UrlErrorCodeGenericBox<T> {
    public var item: T

    public init(item: T) { self.item = item }

    public func echo(_ value: URLError.Code) -> URLError.Code { value }
}

public enum UrlErrorCodeOutcome {
    case failed(URLError.Code)
    case succeeded

    public var failureRawValue: Int {
        if case .failed(let code) = self { return code.rawValue }
        return 0
    }
}

public protocol UrlErrorCodeSource {
    func currentCode() -> URLError.Code
}

public func urlErrorCodeFromSource(_ source: UrlErrorCodeSource) -> Int {
    source.currentCode().rawValue
}

public func urlErrorCodeEcho(_ value: URLError.Code) -> URLError.Code { value }

public func urlErrorCodeRawValue(_ value: URLError.Code) -> Int { value.rawValue }

/// Seven leading `Int64` arguments fill the integer argument registers, so `value` lands at or
/// beyond the last one.
public func urlErrorCodePastRegisterBoundary(
    a0: Int64, a1: Int64, a2: Int64, a3: Int64, a4: Int64, a5: Int64, a6: Int64, value: URLError.Code
) -> Int {
    Int(a0 &+ a1 &+ a2 &+ a3 &+ a4 &+ a5 &+ a6) &+ value.rawValue
}

/// Swift calls the managed closure with a `URLError.Code` and returns what it answers.
public func urlErrorCodeThroughCallback(_ rawValue: Int, _ body: (URLError.Code) -> Int) -> Int {
    body(URLError.Code(rawValue: rawValue))
}

// A URLError.Code beside a parameter that sends the member through a specialized wrapper: a
// closure, an ArraySlice the wrapper rebuilds from an Array, an optional pointer. Each wrapper must
// carry the code as the plain `Int` its @_cdecl lowering uses, never take it under the Swift
// convention, where it arrives by address.

public func urlErrorCodeWithCallback(_ value: URLError.Code, _ body: (Int) -> Int) -> Int {
    body(value.rawValue)
}

public func urlErrorCodeWithSlice(_ value: URLError.Code, _ values: ArraySlice<Int>) -> Int {
    value.rawValue &+ values.reduce(0, &+)
}

public func urlErrorCodeWithOptionalPointer(_ value: URLError.Code, _ addend: UnsafeMutablePointer<Int>?) -> Int {
    value.rawValue &+ (addend?.pointee ?? 0)
}

public final class UrlErrorCodeWrappers {
    public init() {}

    public func withCallback(_ value: URLError.Code, _ body: (Int) -> Int) -> Int { body(value.rawValue) }

    public func withSlice(_ value: URLError.Code, _ values: ArraySlice<Int>) -> Int {
        value.rawValue &+ values.reduce(0, &+)
    }
}
