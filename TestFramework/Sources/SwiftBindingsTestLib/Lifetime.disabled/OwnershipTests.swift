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

/// Resets the allocation counters for testing.
@_cdecl("SwiftBindingsTestLib_ResetAllocationCounters")
public func resetAllocationCounters() {
    counterLock.lock()
    defer { counterLock.unlock() }
    _allocationCounter = 0
    _deallocationCounter = 0
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
public func createMutatingClosure(objectId: Int32) -> () -> String {
    let captured = TrackedObject(objectId: objectId, label: "initial")
    var callCount = 0
    return {
        callCount += 1
        captured.label = "called \(callCount) times"
        return captured.label
    }
}

// MARK: - Async Ownership Testing
// NOTE: Async free functions temporarily disabled. Generator bug: uses `_payload` and `this`
// in static methods.

// /// Creates an object asynchronously and returns it.
// /// Tests that async-returned objects have correct lifetime.
// public func asyncCreateObject(objectId: Int32) async -> TrackedObject {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return TrackedObject(objectId: objectId, label: "async-created")
// }
//
// /// Accepts an object and returns it asynchronously.
// /// Tests that objects passed to async functions retain correctly.
// public func asyncRoundTrip(_ obj: TrackedObject) async -> TrackedObject {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return obj
// }
//
// /// Creates multiple objects asynchronously.
// public func asyncCreateMultiple(count: Int32) async -> [TrackedObject] {
//     try? await Task.sleep(nanoseconds: 1_000_000)
//     return (0..<count).map { TrackedObject(objectId: $0, label: "async-\($0)") }
// }

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
