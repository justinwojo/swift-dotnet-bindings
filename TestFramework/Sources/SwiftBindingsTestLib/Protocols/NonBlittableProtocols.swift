// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum Types for Protocol Testing

/// Status enum for protocol testing.
public enum TaskStatus: Int32 {
    case pending = 0
    case running = 1
    case completed = 2
    case failed = 3
}

/// TaskPriority enum with String raw value for protocol testing.
public enum TaskPriority: String {
    case low = "low"
    case medium = "medium"
    case high = "high"
    case critical = "critical"
}

// MARK: - Protocols with Non-Blittable Properties (Phase 56 Regression Test)

/// Protocol with String properties for witness dispatch testing.
/// Phase 56 fixed protocol conformance validation with non-blittable types.
public protocol Named {
    /// String property getter via witness dispatch.
    var name: String { get }
}

/// Protocol with String getter and setter.
public protocol MutableNamed {
    /// String property with getter and setter via witness dispatch.
    var name: String { get set }
}

/// Protocol with enum property.
public protocol Prioritized {
    /// Enum property getter via witness dispatch.
    var priority: TaskPriority { get }
}

/// Protocol with enum getter and setter.
public protocol MutablePrioritized {
    /// Enum property with getter and setter via witness dispatch.
    var priority: TaskPriority { get set }
}

/// Protocol with both String and enum properties.
public protocol TaskDescriptor {
    var taskName: String { get }
    var status: TaskStatus { get }
    var priority: TaskPriority { get }
}

// MARK: - Protocols with Non-Blittable Methods

/// Protocol with String parameter and return methods.
public protocol StringProcessor {
    /// Method taking String parameter.
    func process(input: String) -> String

    /// Method returning String.
    func getOutput() -> String
}

/// Protocol with enum parameter and return methods.
public protocol StatusHandler {
    /// Method taking enum parameter.
    mutating func handleStatus(_ status: TaskStatus)

    /// Method returning enum.
    func getCurrentStatus() -> TaskStatus

    /// Method with enum parameter and return.
    func transitionStatus(from: TaskStatus) -> TaskStatus
}

/// Protocol with TaskPriority enum methods.
public protocol PriorityHandler {
    /// Method taking TaskPriority parameter.
    mutating func setPriority(_ priority: TaskPriority)

    /// Method returning TaskPriority.
    func getPriority() -> TaskPriority

    /// Method comparing priorities.
    func isHigherPriority(than other: TaskPriority) -> Bool
}

// MARK: - Conforming Types for Testing

/// Struct conforming to Named protocol.
public struct NamedItem: Named {
    public let name: String

    public init(name: String) {
        self.name = name
    }
}

/// Struct conforming to MutableNamed protocol.
public struct MutableNamedItem: MutableNamed {
    public var name: String

    public init(name: String) {
        self.name = name
    }
}

/// Struct conforming to Prioritized protocol.
public struct PrioritizedItem: Prioritized {
    public let priority: TaskPriority

    public init(priority: TaskPriority) {
        self.priority = priority
    }
}

/// Struct conforming to MutablePrioritized protocol.
public struct MutablePrioritizedItem: MutablePrioritized {
    public var priority: TaskPriority

    public init(priority: TaskPriority) {
        self.priority = priority
    }
}

/// Struct conforming to TaskDescriptor protocol.
public struct SimpleTask: TaskDescriptor {
    public let taskName: String
    public let status: TaskStatus
    public let priority: TaskPriority

    public init(taskName: String, status: TaskStatus, priority: TaskPriority) {
        self.taskName = taskName
        self.status = status
        self.priority = priority
    }
}

/// Struct conforming to StringProcessor protocol.
public struct EchoProcessor: StringProcessor {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    public func process(input: String) -> String {
        return "\(prefix): \(input)"
    }

    public func getOutput() -> String {
        return "\(prefix): ready"
    }
}

/// Struct conforming to StatusHandler protocol.
public struct SimpleStatusHandler: StatusHandler {
    private var currentStatus: TaskStatus

    public init(initialStatus: TaskStatus = .pending) {
        self.currentStatus = initialStatus
    }

    public mutating func handleStatus(_ status: TaskStatus) {
        currentStatus = status
    }

    public func getCurrentStatus() -> TaskStatus {
        return currentStatus
    }

    public func transitionStatus(from: TaskStatus) -> TaskStatus {
        switch from {
        case .pending: return .running
        case .running: return .completed
        case .completed: return .completed
        case .failed: return .failed
        }
    }
}

/// Struct conforming to PriorityHandler protocol.
public struct SimplePriorityHandler: PriorityHandler {
    private var currentPriority: TaskPriority

    public init(initialPriority: TaskPriority = .medium) {
        self.currentPriority = initialPriority
    }

    public mutating func setPriority(_ priority: TaskPriority) {
        currentPriority = priority
    }

    public func getPriority() -> TaskPriority {
        return currentPriority
    }

    public func isHigherPriority(than other: TaskPriority) -> Bool {
        let order: [TaskPriority] = [.low, .medium, .high, .critical]
        guard let myIndex = order.firstIndex(of: currentPriority),
              let otherIndex = order.firstIndex(of: other) else {
            return false
        }
        return myIndex > otherIndex
    }
}

// MARK: - Protocol-Constrained Functions

/// Function accepting Named protocol.
public func describeName(_ named: some Named) -> String {
    return "Name: \(named.name)"
}

/// Function accepting Prioritized protocol.
public func describePriority(_ prioritized: some Prioritized) -> String {
    return "Priority: \(prioritized.priority.rawValue)"
}

/// Function accepting TaskDescriptor protocol.
public func describeTask(_ task: some TaskDescriptor) -> String {
    return "[\(task.priority.rawValue)] \(task.taskName): status=\(task.status.rawValue)"
}

/// Function accepting StringProcessor protocol.
public func runProcessor(_ processor: some StringProcessor, input: String) -> String {
    return processor.process(input: input)
}

/// Function accepting StatusHandler protocol.
public func advanceStatus(_ handler: inout some StatusHandler) -> TaskStatus {
    let current = handler.getCurrentStatus()
    let next = handler.transitionStatus(from: current)
    handler.handleStatus(next)
    return next
}

// MARK: - Existential Witness Dispatch (Phase 56 Regression)

// These functions use `any Protocol` (existentials) which force witness table dispatch.
// The `some Protocol` versions above use opaque types (static dispatch).
// Phase 56 fixed witness dispatch with non-blittable types - these exercise that path.

/// Existential function accepting any Named - forces witness table dispatch for String property.
public func describeNameExistential(_ named: any Named) -> String {
    return "Name: \(named.name)"
}

/// Existential function accepting any Prioritized - forces witness table dispatch for enum property.
public func describePriorityExistential(_ prioritized: any Prioritized) -> String {
    return "Priority: \(prioritized.priority.rawValue)"
}

/// Existential function accepting any TaskDescriptor - forces witness dispatch for multiple non-blittable properties.
public func describeTaskExistential(_ task: any TaskDescriptor) -> String {
    return "[\(task.priority.rawValue)] \(task.taskName): status=\(task.status.rawValue)"
}

/// Existential function accepting any StringProcessor - forces witness dispatch for String method params/returns.
public func runProcessorExistential(_ processor: any StringProcessor, input: String) -> String {
    return processor.process(input: input)
}

/// Returns an existential Named (any Named) from a concrete type.
/// Tests existential container creation with String property.
public func asNamedExistential(_ item: NamedItem) -> any Named {
    return item
}

/// Returns an existential Prioritized (any Prioritized) from a concrete type.
/// Tests existential container creation with enum property.
public func asPrioritizedExistential(_ item: PrioritizedItem) -> any Prioritized {
    return item
}

/// Accepts an array of existential Named and describes all.
/// Tests witness dispatch in a loop - exercises repeated witness table lookups.
public func describeAll(_ items: [any Named]) -> [String] {
    return items.map { "Name: \($0.name)" }
}

/// Accepts an array of existential Prioritized and returns all priorities.
/// Tests witness dispatch with enum returns in a loop.
public func getAllPriorities(_ items: [any Prioritized]) -> [TaskPriority] {
    return items.map { $0.priority }
}
