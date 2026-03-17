// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested String Enum (Regression Test for Phase 55)

/// Container struct with nested String enum for testing nested enum emission.
/// Phase 55 fixed issues with String enum FromRawValue() factory methods.
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

// MARK: - Nested Enum Name Collision (Phase 55 Regression)

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
