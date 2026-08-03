// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Async SwiftUI Views whose result monitor resolves to an enum carrying a value, rather
// than to a bare outcome code. One View's result case carries a class, the other's carries
// a resilient struct — the two ownership shapes a payload can cross the callback ABI in.
//
// Shape observed in a document-scanning SDK's UX flow: a View constructed through an async
// chain, holding a monitor that eventually resolves to `.completed(scanResult)`.

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
import SwiftUI

// MARK: - Class payload

/// Result value carried as a class: crosses the ABI as a retained object pointer. Participates
/// in the shared allocation counters, so a callback that never balances the retain it was handed
/// shows up as a named survivor rather than as a test that merely didn't crash.
public final class AsyncResultClassPayload {
    public let code: Int32
    public let label: String

    private let trackedSerial: Int64

    public init(code: Int32, label: String) {
        self.code = code
        self.label = label
        self.trackedSerial = recordTrackedAllocation(
            category: "AsyncResultClassPayload", tag: code)
    }

    deinit {
        recordTrackedDeallocation(serial: trackedSerial)
    }
}

/// Outcome of a monitor whose completed case carries a class.
public enum AsyncClassPayloadOutcome {
    case completed(AsyncResultClassPayload)
    case cancelled
}

/// Async-constructed monitor resolving to a class payload. `preferFastPath` is echoed into
/// the payload so a caller can observe which value the Swift side actually received.
public final class AsyncClassPayloadMonitor {
    public let label: String
    public let preferFastPath: Bool

    init(label: String, preferFastPath: Bool) {
        self.label = label
        self.preferFastPath = preferFastPath
    }

    public static func make(label: String, preferFastPath: Bool) async -> AsyncClassPayloadMonitor {
        AsyncClassPayloadMonitor(label: label, preferFastPath: preferFastPath)
    }

    public func result() async -> AsyncClassPayloadOutcome {
        .completed(AsyncResultClassPayload(code: preferFastPath ? 1 : 0, label: label))
    }
}

/// View whose async chain builds a monitor resolving to a class payload.
public struct AsyncClassPayloadResultView: View {
    public let monitor: AsyncClassPayloadMonitor

    public init(monitor: AsyncClassPayloadMonitor) {
        self.monitor = monitor
    }

    public var body: some View {
        Text("ClassPayload: \(monitor.label)")
    }
}

// MARK: - Struct payload

/// Result value carried as a struct. Not `@frozen`, and the module is built with library
/// evolution, so its layout is only knowable through its metadata and its fields must not be
/// read directly across the ABI. The `String` field keeps the copy non-trivial; the tracked
/// reference makes an unbalanced copy observable — every live copy of this struct holds the
/// same counted object alive, so the live count only returns to zero once every copy on both
/// sides of the ABI has been destroyed.
public struct AsyncResultStructPayload {
    public let count: Int32
    public let name: String
    public let tracker: TrackedRef

    public init(count: Int32, name: String) {
        self.count = count
        self.name = name
        self.tracker = TrackedRef(tag: count, category: "AsyncResultStructPayload")
    }
}

/// Outcome of a monitor whose completed case carries a resilient struct.
public enum AsyncStructPayloadOutcome {
    case completed(AsyncResultStructPayload)
    case cancelled
}

/// Async-constructed monitor resolving to a struct payload. `preferFastPath` is echoed into
/// the payload so a caller can observe which value the Swift side actually received.
public final class AsyncStructPayloadMonitor {
    public let name: String
    public let preferFastPath: Bool

    init(name: String, preferFastPath: Bool) {
        self.name = name
        self.preferFastPath = preferFastPath
    }

    public static func make(name: String, preferFastPath: Bool) async -> AsyncStructPayloadMonitor {
        AsyncStructPayloadMonitor(name: name, preferFastPath: preferFastPath)
    }

    public func result() async -> AsyncStructPayloadOutcome {
        .completed(AsyncResultStructPayload(count: preferFastPath ? 1 : 0, name: name))
    }
}

/// View whose async chain builds a monitor resolving to a struct payload.
public struct AsyncStructPayloadResultView: View {
    public let monitor: AsyncStructPayloadMonitor

    public init(monitor: AsyncStructPayloadMonitor) {
        self.monitor = monitor
    }

    public var body: some View {
        Text("StructPayload: \(monitor.name)")
    }
}
#endif
