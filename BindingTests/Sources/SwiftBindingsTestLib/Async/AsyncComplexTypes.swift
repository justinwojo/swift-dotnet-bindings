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

/// A non-frozen struct for async return testing (Issue #32 regression).
/// Non-frozen structs are projected as C# classes backed by SwiftSafeHandle;
/// the async return path must VWT-copy the Swift-allocated carrier into a
/// C#-owned NativeMemory buffer so that ReleaseHandle's matched-allocator
/// free works and reads survive after the callback returns.
public struct AsyncReport {
    public let title: String
    public let tokenCount: Int32

    public init(title: String, tokenCount: Int32) {
        self.title = title
        self.tokenCount = tokenCount
    }
}

/// A non-frozen struct whose sole property is itself a non-frozen struct.
/// Mirrors the FirebaseAILogic `CountTokensResponse.usageMetadata` shape
/// that exposed the original #32 crash.
public struct AsyncUsageMetadata {
    public let report: AsyncReport

    public init(report: AsyncReport) {
        self.report = report
    }
}

/// An enum for async return testing.
/// Note: Avoid using 'result' as an associated value name as it collides with
/// SwiftIndirectResult parameter in P/Invoke declarations.
public enum AsyncStatus {
    case pending
    case inProgress(progress: Int32)
    case completed(message: String)
    case failed(errorMessage: String)
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

// MARK: - Async String Returns (Regression Test)

/// Struct with async methods returning String values.
/// Regression guard for async String callback marshalling (UTF-8 handling).
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

// MARK: - Async Array Returns (Regression Test)

/// Struct with async methods returning Array<String> values.
/// Regression guard for async Array<String> callback marshalling (buffer serialization).
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

// MARK: - Async Complex Type Returns (Regression Test)

/// Struct with async methods returning complex types (enum, struct, class).
/// Regression guard for async complex type callback marshalling (OpaquePointer handling).
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
        return .completed(message: "Task finished")
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

    /// Async method returning a non-frozen struct (Issue #32 regression).
    /// Before the fix, the Swift-allocated carrier was stored raw in the
    /// C# SafeHandle, causing use-after-free reads and allocator-mismatch
    /// on dispose. The fix VWT-copies into a NativeMemory-owned buffer.
    public func asyncGetReport() async -> AsyncReport {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncReport(title: "Report for \(workerId)", tokenCount: 1234)
    }

    /// Async method returning a non-frozen struct that wraps another
    /// non-frozen struct — the CountTokens-style nested shape.
    public func asyncGetUsageMetadata() async -> AsyncUsageMetadata {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AsyncUsageMetadata(report: AsyncReport(title: "Usage for \(workerId)", tokenCount: 7777))
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

// MARK: - Async Optional Container Returns with ObjC bridge

/// Bug #5 regression: async wrappers returning Optional<Array<ObjCBridgeable>> previously
/// emitted `SwiftMarshal.MarshalFromSwift<SwiftOptional<SwiftArray<IntPtr>>>(...).ToNullable()`
/// which produced a SwiftArray<IntPtr>? where the TaskCompletionSource expected
/// IReadOnlyList<NSUrl>?. Two CS1503 errors blocked StoreKit.ExternalPurchaseLink.getEligibleURLs.
/// The fix routes Optional<Container<ObjCBridgeable>> through the nullable-pointer ABI: read
/// the storage pointer (toll-free bridged to NSArray/NSDict/NSSet), check IntPtr.Zero, and
/// hand the pointer to ArrayFromHandle / GetNSObject.
public class AsyncOptionalContainerWorker {
    public init() {}

    /// Async method returning Optional<Array<URL>> with two URLs.
    public func asyncGetURLArray() async -> [URL]? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return [URL(string: "https://example.com")!, URL(string: "https://test.com")!]
    }

    /// Async method returning Optional<Array<URL>> with no URLs (empty array, not nil).
    public func asyncGetEmptyURLArray() async -> [URL]? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return []
    }

    /// Async method returning Optional<Array<URL>> with nil.
    public func asyncGetNilURLArray() async -> [URL]? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return nil
    }
}

/// Regression: top-level (non-optional) async returns of
/// `Array<ObjCBridgeable>` / `Set<ObjCBridgeable>` / `[Key: ObjCBridgeable]` previously
/// emitted CS1503 because `EmitAsyncWrapperForCollection` declared
/// `var _collection = SwiftMarshal.MarshalFromSwift<SwiftArray<NSUrl>>(resultPtr);`
/// and then handed `_collection` to `ArrayFromHandleFunc<NSUrl>(_collection, ...)`,
/// which expects an IntPtr. The optional path side-stepped this by switching to a
/// `_ptr` carrier (the Swift wrapper stored a +1 retained NSArray/NSDictionary/NSSet
/// via `as AnyObject`); the non-optional path now does the same.
public class AsyncTopLevelObjCContainerWorker {
    public init() {}

    /// Async method returning non-optional `[URL]`.
    public func asyncGetURLArray() async -> [URL] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return [URL(string: "https://example.com")!, URL(string: "https://test.com")!]
    }

    /// Async method returning non-optional `Set<URL>`.
    public func asyncGetURLSet() async -> Set<URL> {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return Set([URL(string: "https://example.com")!, URL(string: "https://test.com")!])
    }

    /// Async method returning non-optional `[String: URL]`.
    public func asyncGetURLDictionary() async -> [String: URL] {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return [
            "primary": URL(string: "https://example.com")!,
            "secondary": URL(string: "https://test.com")!
        ]
    }
}

// MARK: - Async Tuple Returns with Foundation.Data

/// Class with async methods returning tuples containing Foundation.Data.
/// Tests the @convention(c) callback fix: Foundation.Data must be passed via
/// UnsafeMutableRawPointer (heap-allocated) to avoid ABI issues with struct
/// value parameters in C-calling-convention callbacks.
public class AsyncDataWorker {
    public let identifier: String

    public init(identifier: String) {
        self.identifier = identifier
    }

    /// Async method returning (Data, Int) — tests Data in tuple position 0.
    public func fetchDataWithSize() async -> (Foundation.Data, Int) {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let bytes: [UInt8] = Array(identifier.utf8)
        return (Foundation.Data(bytes), bytes.count)
    }

    /// Async method returning (Int, Data) — tests Data in tuple position 1.
    public func fetchSizeWithData() async -> (Int, Foundation.Data) {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let bytes: [UInt8] = Array(identifier.utf8)
        return (bytes.count, Foundation.Data(bytes))
    }

    /// Async method returning (Data, String) — tests Data + String in same tuple.
    public func fetchDataWithLabel() async -> (Foundation.Data, String) {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let bytes: [UInt8] = [0xCA, 0xFE, 0xBA, 0xBE]
        return (Foundation.Data(bytes), identifier)
    }
}

// MARK: - Async Free Functions with Complex Returns
// NOTE: Async free functions temporarily disabled. Generator bug: uses `_payload` and `this`
// in static methods. The struct/class methods above still work (they're instance methods).

// /// Async free function returning struct.
// public func asyncCreateResult(id: Int32, message: String) async -> AsyncResult {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return AsyncResult(id: id, message: message, success: true)
// }
//
// /// Async free function returning class.
// public func asyncCreateTask(taskId: String) async -> AsyncTask {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return AsyncTask(taskId: taskId, status: "pending")
// }
//
// /// Async free function returning enum.
// public func asyncGetCompletionStatus(success: Bool) async -> AsyncStatus {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     if success {
//         return .completed(message: "Success")
//     } else {
//         return .failed(errorMessage: "Failure")
//     }
// }
//
// /// Async free function returning String array.
// public func asyncGenerateStrings(count: Int32) async -> [String] {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return (0..<count).map { "Item \($0)" }
// }
