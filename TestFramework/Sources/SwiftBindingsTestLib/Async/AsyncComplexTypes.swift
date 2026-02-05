// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Supporting Types for Async Returns

/// A frozen struct for async return testing.
@frozen
public struct AsyncResult {
    public let id: Int32
    public let message: String
    public let success: Bool

    public init(id: Int32, message: String, success: Bool) {
        self.id = id
        self.message = message
        self.success = success
    }
}

/// An enum for async return testing.
public enum AsyncStatus {
    case pending
    case inProgress(progress: Int32)
    case completed(result: String)
    case failed(error: String)
}

/// A class for async return testing.
public class AsyncTask {
    public let taskId: String
    public var status: String

    public init(taskId: String, status: String = "created") {
        self.taskId = taskId
        self.status = status
    }

    public func getDescription() -> String {
        return "Task[\(taskId)]: \(status)"
    }
}

// MARK: - Async String Returns (Phase 58 Regression Test)

/// Struct with async methods returning String values.
/// Phase 58 fixed async String callback marshalling (UTF-8 handling).
public struct AsyncStringWorker {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    /// Async method returning a simple String.
    public func asyncGetString() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return "\(prefix): Hello"
    }

    /// Async method returning a String with Unicode.
    public func asyncGetUnicodeString() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return "\(prefix): こんにちは 世界 🌍"
    }

    /// Async method returning an empty String.
    public func asyncGetEmptyString() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return ""
    }

    /// Async method returning a long String.
    public func asyncGetLongString(length: Int32) async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return String(repeating: "A", count: Int(length))
    }

    /// Async static method returning String.
    public static func asyncStaticString() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return "Static async string"
    }
}

// MARK: - Async Array Returns (Phase 59 Regression Test)

/// Struct with async methods returning Array<String> values.
/// Phase 59 fixed async Array<String> callback marshalling (buffer serialization).
public struct AsyncArrayWorker {
    public let identifier: String

    public init(identifier: String) {
        self.identifier = identifier
    }

    /// Async method returning an array of strings.
    public func asyncGetStringArray() async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return ["\(identifier)-first", "\(identifier)-second", "\(identifier)-third"]
    }

    /// Async method returning an empty array.
    public func asyncGetEmptyArray() async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return []
    }

    /// Async method returning a single-element array.
    public func asyncGetSingleElementArray() async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return ["single"]
    }

    /// Async method returning an array with Unicode strings.
    public func asyncGetUnicodeArray() async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return ["Hello", "こんにちは", "안녕하세요", "🎉"]
    }

    /// Async method returning an array of integers.
    public func asyncGetIntArray(count: Int32) async -> [Int32] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return (0..<count).map { $0 }
    }

    /// Async static method returning String array.
    public static func asyncStaticArray() async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return ["static", "array", "result"]
    }
}

// MARK: - Async Complex Type Returns (Phase 60 Regression Test)

/// Struct with async methods returning complex types (enum, struct, class).
/// Phase 60 fixed async complex type callback marshalling (OpaquePointer handling).
public struct AsyncComplexWorker {
    public let workerId: String

    public init(workerId: String) {
        self.workerId = workerId
    }

    /// Async method returning a frozen struct.
    public func asyncGetResult() async -> AsyncResult {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncResult(id: 42, message: "Completed by \(workerId)", success: true)
    }

    /// Async method returning an enum with associated value.
    public func asyncGetStatus() async -> AsyncStatus {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return .completed(result: "Task finished")
    }

    /// Async method returning an enum without associated value.
    public func asyncGetPendingStatus() async -> AsyncStatus {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return .pending
    }

    /// Async method returning a class instance.
    public func asyncGetTask() async -> AsyncTask {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncTask(taskId: workerId, status: "completed async")
    }

    /// Async method returning optional struct (some).
    public func asyncGetOptionalResult() async -> AsyncResult? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncResult(id: 100, message: "Optional result", success: true)
    }

    /// Async method returning optional struct (none).
    public func asyncGetNilResult() async -> AsyncResult? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return nil
    }

    /// Async static method returning complex type.
    public static func asyncStaticResult() async -> AsyncResult {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncResult(id: 0, message: "Static result", success: true)
    }

    /// Async static method returning class.
    public static func asyncStaticTask() async -> AsyncTask {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncTask(taskId: "static-task", status: "created")
    }
}

// MARK: - Async Free Functions with Complex Returns

/// Async free function returning struct.
public func asyncCreateResult(id: Int32, message: String) async -> AsyncResult {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return AsyncResult(id: id, message: message, success: true)
}

/// Async free function returning class.
public func asyncCreateTask(taskId: String) async -> AsyncTask {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return AsyncTask(taskId: taskId, status: "pending")
}

/// Async free function returning enum.
public func asyncGetCompletionStatus(success: Bool) async -> AsyncStatus {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if success {
        return .completed(result: "Success")
    } else {
        return .failed(error: "Failure")
    }
}

/// Async free function returning String array.
public func asyncGenerateStrings(count: Int32) async -> [String] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return (0..<count).map { "Item \($0)" }
}
