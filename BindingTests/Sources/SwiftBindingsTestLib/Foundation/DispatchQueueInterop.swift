// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// Probes for the runtime's hand-written `DispatchQueue` projection.
///
/// `DispatchQueue` is the Swift name of the Objective-C class `OS_dispatch_queue`, so a value of the
/// type is one retained object pointer and the generated binding marshals it by the class
/// convention. These entry points let the C# side check that a queue it obtained from the runtime
/// (main, global) is the very object Swift sees, that a queue Swift returns is adopted as a live
/// wrapper, and that a queue with a real reference count (a custom serial queue) survives the
/// round trip and is released without corruption.
public final class DispatchQueueInteropProbe {
    public init() {}

    /// The label the queue was created with, read on the Swift side.
    public static func label(of queue: DispatchQueue) -> String {
        return queue.label
    }

    /// True when the queue is the process's main queue (object identity, not label).
    public static func isMainQueue(_ queue: DispatchQueue) -> Bool {
        return queue === DispatchQueue.main
    }

    /// True when the queue is the global concurrent queue of the given `qos_class_t` raw value.
    public static func isGlobalQueue(_ queue: DispatchQueue, qosRawValue: UInt32) -> Bool {
        guard let qos = DispatchQoS.QoSClass(rawValue: qos_class_t(rawValue: qosRawValue)) else {
            return false
        }
        return queue === DispatchQueue.global(qos: qos)
    }

    /// Hands the main queue back to C# as a class return.
    public static func mainQueue() -> DispatchQueue {
        return DispatchQueue.main
    }

    /// Hands the default global queue back to C# as a class return.
    public static func defaultGlobalQueue() -> DispatchQueue {
        return DispatchQueue.global()
    }

    /// Creates a fresh serial queue — unlike the main and global queues this one has a real
    /// reference count, so the wrapper's adopt-and-release is observable.
    public static func makeSerialQueue(label: String) -> DispatchQueue {
        return DispatchQueue(label: label)
    }

    /// Runs a block synchronously on the queue and returns twice the value, proving the queue
    /// pointer that arrived from C# is a schedulable queue and not a stale or torn pointer.
    public static func runSync(on queue: DispatchQueue, value: Int) -> Int {
        return queue.sync { value * 2 }
    }
}
