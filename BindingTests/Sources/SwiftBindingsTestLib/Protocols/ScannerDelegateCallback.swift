// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - The scanner-delegate callback shape, as a first-party fixture
//
// This reproduces the SHAPE of a real third-party report rather than the library it came from: a
// framework view-controller-like host owns a `weak var delegate`, and calls back with two arrays
// of an associated-value enum whose cases carry structs holding a UUID identity, a String, and a
// frozen rect-like struct. Every ingredient is what made that callback hard:
//
//  * `weak var delegate` - the C# conformer is reachable only through a non-retaining Swift slot,
//    so the managed object has to stay rooted for as long as the host might call it. That rooting
//    is not this fixture's subject; the fixture CONSUMES it.
//  * `[ScanItem]` in a receiver parameter - an array of an associated-value enum. The array is
//    read through the object-marshalling arm; each element then has to be projected out of the
//    enum's payload, which is where a bitwise read of a borrowed slot corrupts a String or a
//    class reference.
//  * three overloads sharing one base name, distinguished only by argument labels - the naming
//    the delegate protocol convention forces on the emitted C# interface.
//  * a `didTapOn` requirement taking ONE enum by value, so the single-element path is covered
//    beside the array path.
//
// Values are chosen so a truncated or reordered round trip cannot accidentally match: the UUIDs
// are distinct and content-derived, the transcripts differ per element, and the rects carry
// non-integral doubles.

/// Frozen rect-like struct: four `Double`s, no references, so it is blittable and travels inline
/// inside the enum payload. The frozen positive control beside the memory-owning fields.
@frozen
public struct ScanBounds {
    public let x: Double
    public let y: Double
    public let width: Double
    public let height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

/// Tracked class carried inside each payload. It exists so the leak probe has an exact object
/// count to assert; the reported shape itself is all structs and Strings, which own heap storage
/// LifetimeTracker cannot see.
public final class ScanMarker {
    public let serial: Int32

    public init(serial: Int32) {
        self.serial = serial
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Text payload: UUID identity + String + frozen rect + tracked marker.
public struct ScanTextPayload {
    public let id: UUID
    public let transcript: String
    public let bounds: ScanBounds
    public let marker: ScanMarker

    public init(id: UUID, transcript: String, bounds: ScanBounds, marker: ScanMarker) {
        self.id = id
        self.transcript = transcript
        self.bounds = bounds
        self.marker = marker
    }
}

/// Barcode payload: same shape, different field names, so the two enum cases have distinct
/// payload types rather than the same one twice.
public struct ScanBarcodePayload {
    public let id: UUID
    public let symbology: String
    public let bounds: ScanBounds
    public let marker: ScanMarker

    public init(id: UUID, symbology: String, bounds: ScanBounds, marker: ScanMarker) {
        self.id = id
        self.symbology = symbology
        self.bounds = bounds
        self.marker = marker
    }
}

/// Associated-value enum with two payload-carrying cases.
public enum ScanItem {
    case text(ScanTextPayload)
    case barcode(ScanBarcodePayload)
}

// MARK: - Readers
//
// Free functions so C# can inspect a received element without depending on which enum accessors
// the generator chooses to emit. Each reader is total over the enum, so an element that arrived
// with a corrupted discriminator surfaces as the wrong branch rather than as a trap.

/// `"text"` or `"barcode"`.
public func scanItemKind(_ item: ScanItem) -> String {
    switch item {
    case .text: return "text"
    case .barcode: return "barcode"
    }
}

/// The payload's UUID, as its canonical string. Reading it proves the identity word survived.
public func scanItemIdentifier(_ item: ScanItem) -> String {
    switch item {
    case .text(let payload): return payload.id.uuidString
    case .barcode(let payload): return payload.id.uuidString
    }
}

/// The payload's String field - the transcript for `.text`, the symbology for `.barcode`.
public func scanItemLabel(_ item: ScanItem) -> String {
    switch item {
    case .text(let payload): return payload.transcript
    case .barcode(let payload): return payload.symbology
    }
}

/// The payload's frozen rect, rendered so all four doubles have to be intact.
public func scanItemBoundsDescription(_ item: ScanItem) -> String {
    let bounds: ScanBounds
    switch item {
    case .text(let payload): bounds = payload.bounds
    case .barcode(let payload): bounds = payload.bounds
    }
    return "\(bounds.x)/\(bounds.y)/\(bounds.width)/\(bounds.height)"
}

/// The tracked marker's serial.
public func scanItemMarkerSerial(_ item: ScanItem) -> Int32 {
    switch item {
    case .text(let payload): return payload.marker.serial
    case .barcode(let payload): return payload.marker.serial
    }
}

/// One-line rendering of every field, for the whole-element round-trip assertion.
public func describeScanItem(_ item: ScanItem) -> String {
    return scanItemKind(item)
        + "|" + scanItemIdentifier(item)
        + "|" + scanItemLabel(item)
        + "|" + scanItemBoundsDescription(item)
        + "|" + String(scanItemMarkerSerial(item))
}

/// Joins a whole array, so the C# side can assert element ORDER and count in one comparison.
public func describeScanItems(_ items: [ScanItem]) -> String {
    return items.map(describeScanItem).joined(separator: ";")
}

// MARK: - Host + delegate

/// The delegate protocol. Three overloads share the base name `host`, exactly as the delegate
/// convention produces; the emitted C# names have to come from the argument labels.
public protocol ScannerHostDelegate: AnyObject {
    func host(_ host: ScannerHost, didAdd added: [ScanItem], allItems: [ScanItem])
    func host(_ host: ScannerHost, didRemove removed: [ScanItem], allItems: [ScanItem])
    func host(_ host: ScannerHost, didTapOn item: ScanItem)
}

/// The framework object. Holds its delegate WEAKLY, which is the whole reason the reported crash
/// was about lifetime and not only about marshalling.
public final class ScannerHost {
    public weak var delegate: ScannerHostDelegate?

    /// Items the host believes are on screen. Kept so `allItems` is a genuinely different array
    /// from `added`/`removed` rather than the same one passed twice.
    private var items: [ScanItem] = []

    public init() {}

    /// `true` while the weak slot still resolves - the C# side asserts this to separate "the
    /// delegate was collected" from "the callback marshalled wrong".
    public var hasDelegate: Bool {
        return delegate != nil
    }

    /// Builds `count` alternating `.text` / `.barcode` items with deterministic, distinct field
    /// values, appends them to the host's list, and fires `didAdd` with the new items and the full
    /// list. Returns what the host itself sees afterwards, so a callback that consumed or mutated
    /// the borrowed arrays shows up here.
    @discardableResult
    public func emitAdded(count: Int32, seed: Int32) -> String {
        var added: [ScanItem] = []
        for i in 0..<max(0, Int(count)) {
            added.append(makeItem(index: Int32(i), seed: seed))
        }
        items.append(contentsOf: added)
        delegate?.host(self, didAdd: added, allItems: items)
        return describeScanItems(items)
    }

    /// Removes the first `count` items and fires `didRemove` with the removed slice and what is
    /// left. Returns the host's remaining list.
    @discardableResult
    public func emitRemovedFirst(count: Int32) -> String {
        let n = min(max(0, Int(count)), items.count)
        let removed = Array(items.prefix(n))
        items.removeFirst(n)
        delegate?.host(self, didRemove: removed, allItems: items)
        return describeScanItems(items)
    }

    /// Fires `didTapOn` with the item at `index`, then returns that item's description as the host
    /// still sees it.
    @discardableResult
    public func emitTap(index: Int32) -> String {
        guard index >= 0, Int(index) < items.count else { return "" }
        let item = items[Int(index)]
        delegate?.host(self, didTapOn: item)
        return describeScanItem(item)
    }

    /// Fires `didAdd` `iterations` times with the SAME two items, so the leak probe sees a fixed
    /// number of tracked markers across an unbounded number of dispatches.
    @discardableResult
    public func emitAddedRepeatedly(iterations: Int32, seed: Int32) -> String {
        let batch = [makeItem(index: 0, seed: seed), makeItem(index: 1, seed: seed)]
        var last = ""
        for _ in 0..<max(0, Int(iterations)) {
            delegate?.host(self, didAdd: batch, allItems: batch)
            last = describeScanItems(batch)
        }
        return last
    }

    /// Clears the host's list so a test can reset without building a new host.
    public func clearItems() {
        items.removeAll()
    }

    /// The host's current list, for assertions that do not go through a callback.
    public var currentItemsDescription: String {
        return describeScanItems(items)
    }

    /// Deterministic element factory. `seed` and `index` drive every field, so a C# assertion can
    /// recompute the expected description without hard-coding it, and two elements are never
    /// accidentally equal.
    private func makeItem(index: Int32, seed: Int32) -> ScanItem {
        let serial = seed &* 100 &+ index
        let marker = ScanMarker(serial: serial)
        let id = deterministicScanIdentifier(serial: serial)
        let bounds = ScanBounds(
            x: Double(serial) + 0.25,
            y: Double(serial) + 0.5,
            width: Double(serial) + 0.75,
            height: Double(serial) + 1.5)
        if index % 2 == 0 {
            return .text(ScanTextPayload(
                id: id,
                transcript: "text-\(serial)",
                bounds: bounds,
                marker: marker))
        }
        return .barcode(ScanBarcodePayload(
            id: id,
            symbology: "code-\(serial)",
            bounds: bounds,
            marker: marker))
    }
}

/// UUID derived entirely from `serial`, so the C# side can predict the identity of every element
/// without the fixture having to hand it over separately.
public func deterministicScanIdentifier(serial: Int32) -> UUID {
    var bytes = [UInt8](repeating: 0, count: 16)
    let raw = UInt32(bitPattern: serial)
    bytes[12] = UInt8((raw >> 24) & 0xFF)
    bytes[13] = UInt8((raw >> 16) & 0xFF)
    bytes[14] = UInt8((raw >> 8) & 0xFF)
    bytes[15] = UInt8(raw & 0xFF)
    return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3],
                       bytes[4], bytes[5], bytes[6], bytes[7],
                       bytes[8], bytes[9], bytes[10], bytes[11],
                       bytes[12], bytes[13], bytes[14], bytes[15]))
}

/// The canonical string for the identifier the host would build for `serial`, so a C# assertion
/// can compare identity without depending on how `System.Guid` formats bytes.
public func deterministicScanIdentifierString(serial: Int32) -> String {
    return deterministicScanIdentifier(serial: serial).uuidString
}

/// The full description the host would build for the element at `index` of an `emitAdded(count:
/// seed:)` batch, so the C# test asserts against the fixture's own oracle rather than a
/// hand-transcribed literal.
public func expectedScanItemDescription(index: Int32, seed: Int32) -> String {
    let host = ScannerHost()
    host.emitAdded(count: index + 1, seed: seed)
    let all = host.currentItemsDescription.split(separator: ";").map(String.init)
    guard Int(index) < all.count else { return "" }
    return all[Int(index)]
}
