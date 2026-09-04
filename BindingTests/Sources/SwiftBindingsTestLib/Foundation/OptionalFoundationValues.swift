// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Foundation values in property position
//
// `Date?` and `Data?` are the two Foundation values whose C# projection is a VALUE type on the
// far side of the boundary — a Date arrives as a Double and Data as a struct — so the accessor
// cannot lean on a reference becoming null to carry Swift's `nil`. It has to read the carrier's
// own discriminator, or a `nil` reaches the consumer as an empty-but-present value: the Swift
// epoch, or a zero-length byte array, neither of which a consumer can tell from a real one.
//
// This fixture exists to make that difference observable at runtime rather than only in the
// emitted text: both slots can be set from Swift to `nil`, to a genuine value, and to the
// value that LOOKS like the zero (the epoch itself, and an empty `Data`).

/// A box holding one optional `Date` and one optional `Data`, mutated from Swift so the C#
/// side only ever reads the property getters.
public class OptionalFoundationValueBox {
    /// Optional `Date` — projects as `System.DateTimeOffset?` through a Double carrier.
    public var when: Date?

    /// Optional `Data` — projects as `byte[]` through a Swift struct carrier.
    public var blob: Data?

    /// Starts with both slots empty, which is the state the `nil` assertions read.
    public init() {
        self.when = nil
        self.blob = nil
    }

    /// Clears both slots, so a test can go value → nil rather than only observing the initial state.
    public func clear() {
        self.when = nil
        self.blob = nil
    }

    /// Fills both slots: `when` at `secondsSince1970` and `blob` with `byteCount` bytes counting
    /// up from zero. `secondsSince1970: 0` and `byteCount: 0` produce exactly the values a
    /// mishandled `nil` would masquerade as — the epoch and an empty buffer — so a test can
    /// distinguish "Swift said nil" from "Swift said the zero value".
    public func fill(secondsSince1970: Double, byteCount: Int32) {
        self.when = Date(timeIntervalSince1970: secondsSince1970)
        var bytes = [UInt8]()
        var index: Int32 = 0
        while index < byteCount {
            bytes.append(UInt8(index % 251))
            index += 1
        }
        self.blob = Data(bytes)
    }

    /// True while the Swift side still sees a value in each slot — the Swift-side control for the
    /// C# reads, so a test can tell a broken accessor from a fixture that never held anything.
    public var hasWhen: Bool { return when != nil }
    public var hasBlob: Bool { return blob != nil }

    /// The Swift side's own view of the stored values, so a C# read can be compared against what
    /// Swift holds rather than only against what the test asked for.
    public var whenSeconds: Double { return when?.timeIntervalSince1970 ?? -1 }
    public var blobCount: Int32 { return Int32(blob?.count ?? -1) }
}
