// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Deterministic native entry barrier: Dispose occurs after the wrapper has moved its argument,
// before native return. A timeout prevents a failing test from wedging the whole runtime suite.
private final class NoncopyableHandoffGate: @unchecked Sendable {
    let condition = NSCondition()
    var entered = false
    var released = false

    func block() {
        condition.lock()
        entered = true
        condition.broadcast()
        let deadline = Date(timeIntervalSinceNow: 10)
        while !released {
            if !condition.wait(until: deadline) { break }
        }
        condition.unlock()
    }
}

private let noncopyableHandoffGate = NoncopyableHandoffGate()

@_cdecl("SwiftBindingsTestLib_ResetNoncopyableHandoff")
public func resetNoncopyableHandoff() {
    let gate = noncopyableHandoffGate
    gate.condition.lock()
    gate.entered = false
    gate.released = false
    gate.condition.unlock()
}

@_cdecl("SwiftBindingsTestLib_WaitNoncopyableHandoff")
public func waitNoncopyableHandoff() -> Int32 {
    let gate = noncopyableHandoffGate
    gate.condition.lock()
    defer { gate.condition.unlock() }
    let deadline = Date(timeIntervalSinceNow: 10)
    while !gate.entered {
        if !gate.condition.wait(until: deadline) { return 0 }
    }
    return 1
}

@_cdecl("SwiftBindingsTestLib_ReleaseNoncopyableHandoff")
public func releaseNoncopyableHandoff() {
    let gate = noncopyableHandoffGate
    gate.condition.lock()
    gate.released = true
    gate.condition.broadcast()
    gate.condition.unlock()
}

public func borrowTrackedResource(_ resource: borrowing TrackedResource) -> Int32 {
    resource.peek()
}

public func consumeTrackedResourceWithWitness(
    _ resource: consuming TrackedResource, witness: borrowing TrackedResource
) -> Int32 {
    resource.peek() + witness.peek()
}

public func consumeTrackedResourceBlocked(_ resource: consuming TrackedResource) -> Int32 {
    noncopyableHandoffGate.block()
    return resource.peek()
}

public func consumeTrackedResourceBlockedOrThrow(
    _ resource: consuming TrackedResource
) throws -> Int32 {
    noncopyableHandoffGate.block()
    if resource.peek() < 0 { throw TrackedResourceError.rejected }
    return resource.peek()
}
