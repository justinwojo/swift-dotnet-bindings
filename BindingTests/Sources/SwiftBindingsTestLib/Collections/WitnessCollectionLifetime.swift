// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

private let witnessLifetimeLock = NSLock()
private var witnessLifetimeAllocations: Int32 = 0
private var witnessLifetimeDeinits: Int32 = 0

public func resetWitnessLifetimeCounts() {
    witnessLifetimeLock.lock()
    defer { witnessLifetimeLock.unlock() }
    witnessLifetimeAllocations = 0
    witnessLifetimeDeinits = 0
}

public func getWitnessLifetimeAllocations() -> Int32 {
    witnessLifetimeLock.lock()
    defer { witnessLifetimeLock.unlock() }
    return witnessLifetimeAllocations
}

public func getWitnessLifetimeDeinits() -> Int32 {
    witnessLifetimeLock.lock()
    defer { witnessLifetimeLock.unlock() }
    return witnessLifetimeDeinits
}

public final class WitnessLifetimeReference {
    public let identifier: Int32
    internal init(_ identifier: Int32) {
        self.identifier = identifier
        witnessLifetimeLock.lock()
        witnessLifetimeAllocations += 1
        witnessLifetimeLock.unlock()
    }
    deinit {
        witnessLifetimeLock.lock()
        witnessLifetimeDeinits += 1
        witnessLifetimeLock.unlock()
    }
}

@frozen public struct WitnessLifetimeCopy {
    public let reference: WitnessLifetimeReference
    public var identifier: Int32 { reference.identifier }
    internal init(_ identifier: Int32) { reference = WitnessLifetimeReference(identifier) }
}

public struct WitnessLifetimeValue {
    private let reference: WitnessLifetimeReference
    public var identifier: Int32 { reference.identifier }
    internal init(_ identifier: Int32) { reference = WitnessLifetimeReference(identifier) }
}

@frozen public struct WitnessLifetimeInline {
    public let identifier: Int32
    internal init(_ identifier: Int32) { self.identifier = identifier }
}

public struct WitnessLifetimePod: Hashable {
    public let identifier: Int32
    internal init(_ identifier: Int32) { self.identifier = identifier }
}

/// Private backing forces the generated Collection witness path. No element constraints:
/// test-only managed carriers can use the same native metadata for fault injection.
public struct WitnessLifetimeWindow<Element>: RandomAccessCollection {
    private let storage: [Element]
    internal init(_ element: Element) { storage = [element] }
    public var startIndex: Int { 100 }
    public var endIndex: Int { 100 + storage.count }
    public subscript(position: Int) -> Element { storage[position - 100] }
    public func index(after i: Int) -> Int { i + 1 }
    public func index(before i: Int) -> Int { i - 1 }
}

public func makeWitnessCopyWindow() -> WitnessLifetimeWindow<WitnessLifetimeCopy> {
    WitnessLifetimeWindow(WitnessLifetimeCopy(73))
}
public func makeWitnessAdoptWindow() -> WitnessLifetimeWindow<WitnessLifetimeValue> {
    WitnessLifetimeWindow(WitnessLifetimeValue(73))
}
public func makeWitnessClassWindow() -> WitnessLifetimeWindow<WitnessLifetimeReference> {
    WitnessLifetimeWindow(WitnessLifetimeReference(73))
}
public func makeWitnessInlineWindow() -> WitnessLifetimeWindow<WitnessLifetimeInline> {
    WitnessLifetimeWindow(WitnessLifetimeInline(73))
}
public func makeWitnessPodWindow(identifier: Int32) -> WitnessLifetimeWindow<WitnessLifetimePod> {
    WitnessLifetimeWindow(WitnessLifetimePod(identifier))
}
public func makeWitnessStringWindow() -> WitnessLifetimeWindow<String> {
    WitnessLifetimeWindow(String(repeating: "owned collection string ", count: 20))
}
