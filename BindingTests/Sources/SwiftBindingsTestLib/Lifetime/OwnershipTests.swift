// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Lifecycle Tracking

/// Global counter for tracking object allocations.
private var _allocationCounter: Int32 = 0

/// Global counter for tracking object deallocations.
private var _deallocationCounter: Int32 = 0

/// Thread-safe access to allocation counter.
private let counterLock = NSLock()

/// Per-allocation identity for tracked objects that opt into the registry (currently
/// `TrackedRef`), so a leak probe that never balances can NAME the survivors instead of
/// reporting only a count. Keyed by a monotonic serial; an entry is inserted at allocation
/// and removed at deallocation, so whatever remains after a drain is exactly what leaked.
/// The info is a pure value (no reference to the tracked object) — the registry must never
/// retain a tracked object, or it would itself prevent the deinit it exists to observe.
/// Guarded by the same `counterLock` as the counters, so the registry and the counts can
/// never disagree. Only registry-aware callers populate this; the plain counter-only
/// `recordTrackedAllocation()` / `recordTrackedDeallocation()` overloads leave it empty.
private struct TrackedLiveInfo {
    let serial: Int64
    let category: String
    let tag: Int32
    let allocOrder: Int32
}

private var _liveTracked: [Int64: TrackedLiveInfo] = [:]
private var _nextTrackedSerial: Int64 = 0

/// Resets the allocation counters for testing.
@_cdecl("SwiftBindingsTestLib_ResetAllocationCounters")
public func resetAllocationCounters() {
    counterLock.lock()
    defer { counterLock.unlock() }
    _allocationCounter = 0
    _deallocationCounter = 0
    _liveTracked.removeAll(keepingCapacity: true)
    _nextTrackedSerial = 0
}

/// Gets the current allocation count.
@_cdecl("SwiftBindingsTestLib_GetAllocationCount")
public func getAllocationCount() -> Int32 {
    counterLock.lock()
    defer { counterLock.unlock() }
    return _allocationCounter
}

/// Gets the current deallocation count.
@_cdecl("SwiftBindingsTestLib_GetDeallocationCount")
public func getDeallocationCount() -> Int32 {
    counterLock.lock()
    defer { counterLock.unlock() }
    return _deallocationCounter
}

/// Gets the current live object count (allocations - deallocations).
@_cdecl("SwiftBindingsTestLib_GetLiveObjectCount")
public func getLiveObjectCount() -> Int32 {
    counterLock.lock()
    defer { counterLock.unlock() }
    return _allocationCounter - _deallocationCounter
}

/// Internal hooks so tracked fixtures defined in OTHER files (e.g. the
/// struct-with-ref fixtures in MemoryManagement/LeakDetection.swift) feed the
/// same allocation counters that `LifetimeTracker` reads. The counters are
/// file-private; these record functions are the cross-file seam.
func recordTrackedAllocation() {
    counterLock.lock()
    _allocationCounter += 1
    counterLock.unlock()
}

func recordTrackedDeallocation() {
    counterLock.lock()
    _deallocationCounter += 1
    counterLock.unlock()
}

/// Registry-aware allocation record: bumps the same counters AND registers per-allocation
/// identity, returning the serial the caller must hand back at deallocation. Used by
/// `TrackedRef` so a leak survivor can be named. `category`/`tag`/`allocOrder` are all the
/// registry keeps — never a reference to the object — so registration cannot pin it alive.
func recordTrackedAllocation(category: String, tag: Int32) -> Int64 {
    counterLock.lock()
    defer { counterLock.unlock() }
    _allocationCounter += 1
    _nextTrackedSerial += 1
    let serial = _nextTrackedSerial
    _liveTracked[serial] = TrackedLiveInfo(
        serial: serial, category: category, tag: tag, allocOrder: _allocationCounter)
    return serial
}

/// Registry-aware deallocation record: bumps the same counters AND drops the live entry the
/// matching `recordTrackedAllocation(category:tag:)` registered.
func recordTrackedDeallocation(serial: Int64) {
    counterLock.lock()
    defer { counterLock.unlock() }
    _deallocationCounter += 1
    _liveTracked.removeValue(forKey: serial)
}

/// Describes the tracked objects still live, for a leak probe that never balanced. Returns a
/// `strdup`'d C string the caller must free with `SwiftBindingsTestLib_FreeString`. When the
/// registry is empty the live count is still reported so the caller can distinguish "leaked
/// in an un-instrumented category" (live > 0, no survivors listed) from "balanced" (live 0).
@_cdecl("SwiftBindingsTestLib_DumpLiveTrackedObjects")
public func dumpLiveTrackedObjects() -> UnsafeMutablePointer<CChar>? {
    counterLock.lock()
    let survivors = _liveTracked.values.sorted { $0.allocOrder < $1.allocOrder }
    let live = _allocationCounter - _deallocationCounter
    counterLock.unlock()

    if survivors.isEmpty {
        return strdup(
            "live=\(live); no registry-tracked survivors "
                + "(any leak is in a category that does not register per-object identity)")
    }

    // Cap the listing so a large leak can't build an unbounded string.
    let cap = 32
    let listed = survivors.prefix(cap).map {
        "{serial=\($0.serial) category=\($0.category) tag=\($0.tag) allocOrder=\($0.allocOrder)}"
    }
    var summary = "live=\(live); registry-tracked survivors=\(survivors.count): "
        + listed.joined(separator: ", ")
    if survivors.count > cap {
        summary += ", …(+\(survivors.count - cap) more)"
    }
    return strdup(summary)
}

/// Frees a C string returned by `SwiftBindingsTestLib_DumpLiveTrackedObjects`.
@_cdecl("SwiftBindingsTestLib_FreeString")
public func freeTrackedString(_ ptr: UnsafeMutablePointer<CChar>?) {
    if let ptr = ptr {
        free(ptr)
    }
}

// MARK: - Tracked Classes

/// A class that tracks its own allocation and deallocation.
/// Use this to verify retain/release balance in C# bindings.
public class TrackedObject {
    public let objectId: Int32
    public var label: String

    public init(objectId: Int32, label: String = "default") {
        self.objectId = objectId
        self.label = label
        counterLock.lock()
        _allocationCounter += 1
        counterLock.unlock()
    }

    deinit {
        counterLock.lock()
        _deallocationCounter += 1
        counterLock.unlock()
    }

    /// Simple method to verify object is alive.
    public func isAlive() -> Bool {
        return true
    }

    /// Returns a description string.
    public func describe() -> String {
        return "TrackedObject[\(objectId)]: \(label)"
    }
}

/// A class with a reference to another tracked object.
/// Use this to test reference chains and ownership.
public class TrackedContainer {
    public let containerId: Int32
    public var child: TrackedObject?

    public init(containerId: Int32, child: TrackedObject? = nil) {
        self.containerId = containerId
        self.child = child
        counterLock.lock()
        _allocationCounter += 1
        counterLock.unlock()
    }

    deinit {
        counterLock.lock()
        _deallocationCounter += 1
        counterLock.unlock()
    }

    /// Sets the child object.
    public func setChild(_ child: TrackedObject?) {
        self.child = child
    }

    /// Gets the child object.
    public func getChild() -> TrackedObject? {
        return child
    }

    /// Returns whether this container has a child.
    public func hasChild() -> Bool {
        return child != nil
    }
}

// MARK: - Factory Functions

/// Creates a tracked object with the given ID.
public func createTrackedObject(objectId: Int32) -> TrackedObject {
    return TrackedObject(objectId: objectId)
}

/// Creates a tracked object with label.
public func createTrackedObject(objectId: Int32, label: String) -> TrackedObject {
    return TrackedObject(objectId: objectId, label: label)
}

/// Creates a container with an optional child.
public func createTrackedContainer(containerId: Int32, childId: Int32?) -> TrackedContainer {
    let child: TrackedObject?
    if let childId = childId {
        child = TrackedObject(objectId: childId)
    } else {
        child = nil
    }
    return TrackedContainer(containerId: containerId, child: child)
}

/// Creates multiple tracked objects and returns them in an array.
public func createMultipleObjects(count: Int32) -> [TrackedObject] {
    return (0..<count).map { TrackedObject(objectId: $0) }
}

// MARK: - Reference Semantics Testing

/// Returns the same object passed in (tests reference identity).
public func identity(_ obj: TrackedObject) -> TrackedObject {
    return obj
}

/// Creates a new object that copies properties from the source.
public func clone(_ obj: TrackedObject) -> TrackedObject {
    return TrackedObject(objectId: obj.objectId, label: obj.label + " (clone)")
}

/// Stores an object in a static variable (extends lifetime).
private var _storedObject: TrackedObject?

/// Stores an object for later retrieval (extends lifetime beyond caller).
public func storeObject(_ obj: TrackedObject) {
    _storedObject = obj
}

/// Retrieves the stored object.
public func retrieveStoredObject() -> TrackedObject? {
    return _storedObject
}

/// Clears the stored object.
public func clearStoredObject() {
    _storedObject = nil
}

// MARK: - Closure Capture Testing

/// Returns a closure that captures a tracked object.
/// Use this to test closure lifetime and captured object ownership.
public func createCapturingClosure(objectId: Int32) -> () -> Int32 {
    let captured = TrackedObject(objectId: objectId)
    return {
        return captured.objectId
    }
}

/// Returns a closure that captures and modifies a tracked object.
/// Returns call count (Int32) instead of String to avoid closure-return-String generator bug.
public func createMutatingClosure(objectId: Int32) -> () -> Int32 {
    let captured = TrackedObject(objectId: objectId, label: "initial")
    var callCount: Int32 = 0
    return {
        callCount += 1
        captured.label = "called \(callCount) times"
        return callCount
    }
}

// MARK: - Async Ownership Testing

/// Creates an object asynchronously and returns it.
/// Tests that async-returned objects have correct lifetime.
public func asyncCreateObject(objectId: Int32) async -> TrackedObject {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return TrackedObject(objectId: objectId, label: "async-created")
}

/// Accepts an object and returns it asynchronously.
/// Tests that objects passed to async functions retain correctly.
public func asyncRoundTrip(_ obj: TrackedObject) async -> TrackedObject {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return obj
}

// MARK: - Protocol-Based Ownership

/// Protocol for objects that can be owned.
public protocol Ownable {
    var ownerId: Int32 { get }
}

extension TrackedObject: Ownable {
    public var ownerId: Int32 {
        return objectId
    }
}

/// Accepts any Ownable and returns its ID.
public func getOwnerId(_ ownable: some Ownable) -> Int32 {
    return ownable.ownerId
}

// MARK: - Value Type Reference Comparison

/// A value type struct for comparison with reference types.
@frozen
public struct ValuePoint {
    public var x: Int32
    public var y: Int32

    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }
}

/// Creates a ValuePoint (no allocation tracking).
public func createValuePoint(x: Int32, y: Int32) -> ValuePoint {
    return ValuePoint(x: x, y: y)
}

/// Modifies a copy of the value point (demonstrates value semantics).
public func modifyValuePoint(_ point: ValuePoint, newX: Int32) -> ValuePoint {
    var copy = point
    copy.x = newX
    return copy
}
