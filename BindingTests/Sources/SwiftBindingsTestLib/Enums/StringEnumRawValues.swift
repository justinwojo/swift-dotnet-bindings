// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested String Enum

/// Container struct with nested String enum for testing nested enum emission.
/// Regression guard for String enum FromRawValue() factory methods.
public struct NetworkConfig {
    /// Nested String enum for HTTP methods.
    public enum HttpMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
        case patch = "PATCH"
    }

    /// Nested String enum for content types.
    public enum ContentType: String {
        case json = "application/json"
        case xml = "application/xml"
        case formData = "multipart/form-data"
        case text = "text/plain"
    }

    public let method: HttpMethod
    public let contentType: ContentType
    public let url: String

    public init(method: HttpMethod, contentType: ContentType, url: String) {
        self.method = method
        self.contentType = contentType
        self.url = url
    }
}

// MARK: - Nested Enum Name Collision

/// First container with nested `Status` enum.
/// Tests that generated C# correctly scopes nested enum names.
public struct OrderContainer {
    /// Nested Status enum - same name as in PaymentContainer.
    public enum Status: String {
        case pending = "order_pending"
        case processing = "order_processing"
        case shipped = "order_shipped"
        case delivered = "order_delivered"
        case cancelled = "order_cancelled"
    }

    public let orderId: String
    public let status: Status

    public init(orderId: String, status: Status) {
        self.orderId = orderId
        self.status = status
    }
}

/// Second container with nested `Status` enum - same name as OrderContainer.Status.
/// This collision exercises name-scoping in generated FromRawValue() factories.
public struct PaymentContainer {
    /// Nested Status enum - same name as in OrderContainer.
    public enum Status: String {
        case pending = "payment_pending"
        case authorized = "payment_authorized"
        case captured = "payment_captured"
        case refunded = "payment_refunded"
        case failed = "payment_failed"
    }

    public let paymentId: String
    public let status: Status

    public init(paymentId: String, status: Status) {
        self.paymentId = paymentId
        self.status = status
    }
}

/// Creates an OrderContainer from raw status string.
/// Tests OrderContainer.Status.FromRawValue() generation.
public func createOrder(orderId: String, statusRaw: String) -> OrderContainer? {
    guard let status = OrderContainer.Status(rawValue: statusRaw) else {
        return nil
    }
    return OrderContainer(orderId: orderId, status: status)
}

/// Creates a PaymentContainer from raw status string.
/// Tests PaymentContainer.Status.FromRawValue() generation - must not collide with OrderContainer.Status.
public func createPayment(paymentId: String, statusRaw: String) -> PaymentContainer? {
    guard let status = PaymentContainer.Status(rawValue: statusRaw) else {
        return nil
    }
    return PaymentContainer(paymentId: paymentId, status: status)
}

/// Extracts raw status from OrderContainer.
public func getOrderStatusRaw(_ order: OrderContainer) -> String {
    return order.status.rawValue
}

/// Extracts raw status from PaymentContainer.
public func getPaymentStatusRaw(_ payment: PaymentContainer) -> String {
    return payment.status.rawValue
}

// MARK: - String Enum with Special Characters

/// String enum with special characters in raw values.
public enum LogLevel: String {
    case debug = "[DEBUG]"
    case info = "[INFO]"
    case warning = "[WARN]"
    case error = "[ERROR]"
    case critical = "[CRITICAL]"
}

// MARK: - String Enum with Unicode

/// String enum with Unicode raw values for internationalization.
public enum Greeting: String {
    case english = "Hello"
    case japanese = "こんにちは"
    case korean = "안녕하세요"
    case emoji = "👋"
    case mixed = "Hello 世界!"
}

// MARK: - String Enum with Empty/Whitespace

/// String enum testing edge cases in raw values.
public enum EdgeCaseStrings: String {
    case empty = ""
    case singleSpace = " "
    case multipleSpaces = "   "
    case newline = "\n"
    case tab = "\t"
    case normal = "normal"
}

// MARK: - String Enum Factory Round-Trip Functions

/// Creates a LogLevel from its raw value and returns the enum.
/// This exercises the FromRawValue() factory method.
public func createLogLevel(from rawValue: String) -> LogLevel? {
    return LogLevel(rawValue: rawValue)
}

/// Gets the raw value from a LogLevel and returns it.
/// Validates that raw value round-trips correctly.
public func getLogLevelRaw(_ level: LogLevel) -> String {
    return level.rawValue
}

/// Full round-trip test: raw value -> enum -> raw value.
/// Returns true if the values match, false otherwise.
public func validateLogLevelRoundTrip(_ rawValue: String) -> Bool {
    guard let level = LogLevel(rawValue: rawValue) else {
        return false
    }
    return level.rawValue == rawValue
}

/// Creates a Greeting from its raw value for Unicode testing.
public func createGreeting(from rawValue: String) -> Greeting? {
    return Greeting(rawValue: rawValue)
}

/// Full round-trip for Unicode string enum.
public func validateGreetingRoundTrip(_ rawValue: String) -> Bool {
    guard let greeting = Greeting(rawValue: rawValue) else {
        return false
    }
    return greeting.rawValue == rawValue
}

// MARK: - Nested Enum Access Patterns

/// Creates a NetworkConfig with specified method string.
/// Tests nested enum factory access pattern.
public func createNetworkConfig(methodRaw: String, contentTypeRaw: String, url: String) -> NetworkConfig? {
    guard let method = NetworkConfig.HttpMethod(rawValue: methodRaw),
          let contentType = NetworkConfig.ContentType(rawValue: contentTypeRaw) else {
        return nil
    }
    return NetworkConfig(method: method, contentType: contentType, url: url)
}

/// Extracts the method raw value from a NetworkConfig.
public func getMethodRaw(_ config: NetworkConfig) -> String {
    return config.method.rawValue
}

/// Extracts the content type raw value from a NetworkConfig.
public func getContentTypeRaw(_ config: NetworkConfig) -> String {
    return config.contentType.rawValue
}

// MARK: - String Enum with Case-Sensitive Collision Potential

/// String enum with values that could collide if case handling is wrong.
public enum CaseSensitiveEnum: String {
    case lower = "value"
    case upper = "VALUE"
    case mixed = "Value"
    case camel = "valueCase"
    case pascal = "ValueCase"
}

/// Validates case sensitivity is preserved in round-trip.
public func validateCaseSensitiveRoundTrip(_ rawValue: String) -> Bool {
    guard let value = CaseSensitiveEnum(rawValue: rawValue) else {
        return false
    }
    return value.rawValue == rawValue
}

// MARK: - String-Raw Enum Returns Across the Wrapper Boundary

/// String-raw enum returned by value from members that take the generated `@_cdecl` wrapper
/// route. A `String` raw value is not a C-ABI scalar, so `.rawValue` is not what crosses the
/// boundary: the case ordinal does, matching the `int` underlying type the managed enum
/// declares. Covers the non-throwing conversion and the throwing wrapper's catch-block
/// sentinel on both the method and the property emitter.
@frozen public enum TransferMode: String {
    case fast = "fast"
    case slow = "slow"
    case idle = "idle"
}

/// Exercises a String-raw enum return from the member kinds that reach the `@_cdecl` wrapper
/// emitters. `pick` is the control that stays on the native-thunk route; `pickChecked` carries
/// an `inout` parameter, which the thunk refuses, so its throwing body is emitted by the
/// method wrapper emitter.
public final class TransferModeSelector {
    private let fallback: TransferMode

    public init(fallback: String) {
        self.fallback = TransferMode(rawValue: fallback) ?? .idle
    }

    /// Non-throwing method returning a String-raw enum by value (native-thunk control).
    public func pick(_ wantFast: Bool) -> TransferMode {
        return wantFast ? .fast : .slow
    }

    /// Throwing method returning a String-raw enum by value, with an `inout` parameter so the
    /// member takes the wrapper route and reaches the throwing wrapper's catch-block sentinel.
    public func pickChecked(_ attempts: inout Int32, wantFast: Bool) throws -> TransferMode {
        attempts += 1
        if fallback == .idle {
            throw TransferModeError.unavailable
        }
        return wantFast ? .fast : .slow
    }
}

/// Non-frozen struct parent: its property accessors use opaque (indirect-buffer) conventions
/// that the native thunk refuses, so the String-raw enum getters are emitted by the property
/// wrapper emitter — including the throwing getter's catch-block sentinel.
public struct TransferModeBox {
    private let stored: TransferMode

    public init(mode: String) {
        self.stored = TransferMode(rawValue: mode) ?? .idle
    }

    /// Non-throwing property returning a String-raw enum by value.
    public var preferred: TransferMode {
        return stored
    }

    /// Throwing property returning a String-raw enum by value.
    public var validated: TransferMode {
        get throws {
            if stored == .idle {
                throw TransferModeError.unavailable
            }
            return stored
        }
    }
}

public enum TransferModeError: Error {
    case unavailable
}
