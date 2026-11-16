// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum payload ownership (TryGet extraction)
//
// A generated `TryGet<Case>` copies the enum (`InitializeWithCopy`, +1 on every reference in the
// payload), projects the payload out of the copy and never value-witness destroys the copy. Each
// payload shape therefore has to consume that +1 exactly once: adopt it into the wrapper it
// returns, or release it after copying the value out. Every payload here carries something the
// shared allocation registry counts (`LifetimeTracker`), so a test sees a reference that was
// never released and one that was released too early alike.

/// Objective-C-rooted object carried as a plain `NSObject` payload, so the binding reads it
/// through the Objective-C class arm rather than as a class of this module.
public final class EnumPayloadTrackedObject: NSObject {
    public let tag: Int32
    private let trackedSerial: Int64

    public init(tag: Int32) {
        self.tag = tag
        self.trackedSerial = recordTrackedAllocation(category: "EnumPayloadTrackedObject", tag: tag)
        super.init()
    }

    deinit {
        recordTrackedDeallocation(serial: trackedSerial)
    }
}

public enum EnumPayloadOwnership {
    case list([TrackedRef])
    case maybe(TrackedRef?)
    case outcome(Result<TrackedRef, TrackedRefError>)
    case bytes(Data)
    case object(NSObject)
    case pair(TrackedRef?, Data)
    case maybeObject(NSObject?)
    case objects([NSObject])
}

/// Data whose out-of-line storage is counted: the deallocator records the release, so a payload
/// the binding never released keeps the count up. Large enough to stay out of line.
private func trackedPayloadBytes(count: Int) -> Data {
    let serial = recordTrackedAllocation(category: "EnumPayloadBytes", tag: Int32(count))
    let bytes = UnsafeMutableRawPointer.allocate(byteCount: count, alignment: 1)
    for i in 0..<count {
        bytes.storeBytes(of: UInt8(truncatingIfNeeded: i * 3 + 1), toByteOffset: i, as: UInt8.self)
    }
    return Data(bytesNoCopy: bytes, count: count, deallocator: .custom { pointer, _ in
        pointer.deallocate()
        recordTrackedDeallocation(serial: serial)
    })
}

public func enumPayloadOwnershipList(tag: Int32) -> EnumPayloadOwnership {
    .list([TrackedRef(tag: tag, category: "EnumPayloadList"), TrackedRef(tag: tag + 1, category: "EnumPayloadList")])
}

public func enumPayloadOwnershipMaybe(tag: Int32) -> EnumPayloadOwnership {
    .maybe(TrackedRef(tag: tag, category: "EnumPayloadMaybe"))
}

public func enumPayloadOwnershipOutcome(tag: Int32) -> EnumPayloadOwnership {
    .outcome(.success(TrackedRef(tag: tag, category: "EnumPayloadOutcome")))
}

public func enumPayloadOwnershipBytes(count: Int32) -> EnumPayloadOwnership {
    .bytes(trackedPayloadBytes(count: Int(count)))
}

public func enumPayloadOwnershipObject(tag: Int32) -> EnumPayloadOwnership {
    .object(EnumPayloadTrackedObject(tag: tag))
}

public func enumPayloadOwnershipPair(tag: Int32, count: Int32) -> EnumPayloadOwnership {
    .pair(TrackedRef(tag: tag, category: "EnumPayloadPair"), trackedPayloadBytes(count: Int(count)))
}

public func enumPayloadOwnershipMaybeObject(tag: Int32) -> EnumPayloadOwnership {
    .maybeObject(EnumPayloadTrackedObject(tag: tag))
}

public func enumPayloadOwnershipObjects(tag: Int32) -> EnumPayloadOwnership {
    .objects([EnumPayloadTrackedObject(tag: tag), EnumPayloadTrackedObject(tag: tag + 1)])
}
