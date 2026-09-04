// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Borrowed-slot reverse-callback parameters
//
// When Swift dispatches back into a C# protocol implementation, every parameter arrives as the
// ADDRESS of a slot the Swift conformance owns: it copies the argument into its own local and
// deinitializes that local as soon as the receiver returns. So the generated proxy receiver has to
// read each slot in a way that (a) produces a value the managed side may keep afterwards and
// (b) leaves the source slot untouched.
//
// A plain bitwise read satisfies neither for two whole families of parameter:
//
//   • Managed-wrapper value types — a non-frozen struct, an associated-value enum, or an Optional
//     of either. These project to C# wrapper CLASSES, so reading the slot bitwise reinterprets
//     Swift's first payload word as a managed object reference (garbage ref → crash on first use).
//     `BorrowedSlotRecord` and `BorrowedSlotItem` below carry the shape: a struct mixing a class
//     field with a String field, and an enum with one class-payload case and one struct-payload
//     case.
//
//   • Payload-free enums — Swift stores the discriminator of a small enum in ONE byte, while the
//     C# carrier is `enum : int`. A four-byte read of a one-byte slot drags in three bytes of the
//     neighbouring value, so the receiver sees a case the caller never passed. `BorrowedSlotKind`
//     below is that shape.
//
// The drivers deliberately keep using the ORIGINAL values after the callback returns and hand the
// caller a summary of them, so a receiver that consumed or destroyed the borrowed source shows up
// as a corrupted summary rather than as silent luck. The class payload feeds the shared
// LifetimeTracker counters (`recordTrackedAllocation` / `recordTrackedDeallocation`, defined in
// Lifetime/OwnershipTests.swift) so repeated dispatch can be asserted for ARC balance too.

// MARK: - Payload types

/// Tracked class payload. Reaching it through a struct field or an enum case after the callback
/// is what proves the copy-out took a real reference rather than a reinterpreted word.
public final class BorrowedSlotRef {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Non-frozen struct mixing a class field with a String field — two reference-holding fields plus
/// a scalar, so a bitwise read has three different ways to go wrong.
public struct BorrowedSlotRecord {
    public let ref: BorrowedSlotRef
    public let name: String
    public let code: Int32

    public init(ref: BorrowedSlotRef, name: String, code: Int32) {
        self.ref = ref
        self.name = name
        self.code = code
    }
}

/// Associated-value enum with a class-payload case and a struct-payload case — the shape of a
/// scanned-item delegate callback, and the one that projects to an adopting wrapper class.
public enum BorrowedSlotItem {
    case tracked(BorrowedSlotRef)
    case record(BorrowedSlotRecord)
    case blank
}

/// Payload-free enum: Swift stores this discriminator in a single byte.
public enum BorrowedSlotKind {
    case alpha
    case beta
    case gamma
}

// MARK: - Value readers
//
// Free functions rather than members so the C# side can describe a received value without
// depending on which members of a wrapper type the generator chooses to bind.

/// Renders a record's three fields. Reading `ref.tag` dereferences the class field, which is the
/// operation that faults when the slot was read bitwise.
public func describeBorrowedRecord(_ record: BorrowedSlotRecord) -> String {
    return "\(record.name)#\(record.code)/\(record.ref.tag)"
}

/// Renders an item by case, reaching into each case's payload.
public func describeBorrowedItem(_ item: BorrowedSlotItem) -> String {
    switch item {
    case .tracked(let ref): return "tracked/\(ref.tag)"
    case .record(let record): return "record/" + describeBorrowedRecord(record)
    case .blank: return "blank"
    }
}

/// Renders a payload-free enum case. A discriminator read at the wrong width lands outside the
/// declared cases, so this is where an over-read surfaces.
public func describeBorrowedKind(_ kind: BorrowedSlotKind) -> String {
    switch kind {
    case .alpha: return "alpha"
    case .beta: return "beta"
    case .gamma: return "gamma"
    }
}

public func describeOptionalBorrowedRecord(_ record: BorrowedSlotRecord?) -> String {
    guard let record else { return "nil" }
    return describeBorrowedRecord(record)
}

public func describeOptionalBorrowedItem(_ item: BorrowedSlotItem?) -> String {
    guard let item else { return "nil" }
    return describeBorrowedItem(item)
}

// MARK: - Reverse-dispatch protocol

/// Class-bound protocol whose requirements take each borrowed-slot parameter shape. The generated
/// `IBorrowedSlotReceiver` is what the C# test implements; Swift calls back into it through the
/// generated proxy receiver.
public protocol BorrowedSlotReceiver: AnyObject {
    /// Non-frozen struct with a class field and a String field.
    func onRecord(_ record: BorrowedSlotRecord)
    /// Associated-value enum.
    func onItem(_ item: BorrowedSlotItem)
    /// Payload-free one-byte enum.
    func onKind(_ kind: BorrowedSlotKind)
    /// Optional of the struct.
    func onOptionalRecord(_ record: BorrowedSlotRecord?)
    /// Optional of the enum.
    func onOptionalItem(_ item: BorrowedSlotItem?)
}

/// Synchronous driver: builds values Swift-side, dispatches them on the same thread (so the C#
/// test needs no runloop pumping), then re-reads the ORIGINALS and returns a summary of them.
public final class BorrowedSlotDriver {
    public init() {}

    /// Dispatches a record, then describes the original — the original must be unchanged and
    /// still readable after the receiver has returned.
    public func driveRecord(_ receiver: BorrowedSlotReceiver, name: String, code: Int32, tag: Int32) -> String {
        let record = BorrowedSlotRecord(ref: BorrowedSlotRef(tag: tag), name: name, code: code)
        receiver.onRecord(record)
        return describeBorrowedRecord(record)
    }

    /// Dispatches the class-payload case of the enum, then describes the original.
    public func driveTrackedItem(_ receiver: BorrowedSlotReceiver, tag: Int32) -> String {
        let item = BorrowedSlotItem.tracked(BorrowedSlotRef(tag: tag))
        receiver.onItem(item)
        return describeBorrowedItem(item)
    }

    /// Dispatches the struct-payload case of the enum, then describes the original.
    public func driveRecordItem(_ receiver: BorrowedSlotReceiver, name: String, code: Int32, tag: Int32) -> String {
        let record = BorrowedSlotRecord(ref: BorrowedSlotRef(tag: tag), name: name, code: code)
        let item = BorrowedSlotItem.record(record)
        receiver.onItem(item)
        return describeBorrowedItem(item)
    }

    /// Dispatches the payload-free case, then describes the original.
    public func driveBlankItem(_ receiver: BorrowedSlotReceiver) -> String {
        let item = BorrowedSlotItem.blank
        receiver.onItem(item)
        return describeBorrowedItem(item)
    }

    /// Dispatches a one-byte enum discriminator, then describes the original.
    public func driveKind(_ receiver: BorrowedSlotReceiver, kind: BorrowedSlotKind) -> String {
        receiver.onKind(kind)
        return describeBorrowedKind(kind)
    }

    /// Dispatches a non-nil Optional struct, then describes the original.
    public func driveOptionalRecord(_ receiver: BorrowedSlotReceiver, name: String, code: Int32, tag: Int32) -> String {
        let record: BorrowedSlotRecord? = BorrowedSlotRecord(ref: BorrowedSlotRef(tag: tag), name: name, code: code)
        receiver.onOptionalRecord(record)
        return describeOptionalBorrowedRecord(record)
    }

    /// Dispatches a nil Optional struct.
    public func driveNilRecord(_ receiver: BorrowedSlotReceiver) -> String {
        receiver.onOptionalRecord(nil)
        return "nil"
    }

    /// Dispatches a non-nil Optional enum, then describes the original.
    public func driveOptionalItem(_ receiver: BorrowedSlotReceiver, tag: Int32) -> String {
        let item: BorrowedSlotItem? = .tracked(BorrowedSlotRef(tag: tag))
        receiver.onOptionalItem(item)
        return describeOptionalBorrowedItem(item)
    }

    /// Dispatches a nil Optional enum.
    public func driveNilItem(_ receiver: BorrowedSlotReceiver) -> String {
        receiver.onOptionalItem(nil)
        return "nil"
    }

    /// Repeats the record dispatch so the C# side can watch the tracked-class live count return
    /// to its starting value: a copy-out that over-retains strands one payload per iteration, and
    /// one that under-retains frees a payload the original still owns.
    public func driveRecordRepeatedly(_ receiver: BorrowedSlotReceiver, iterations: Int32, tag: Int32) -> String {
        var last = ""
        for i in 0..<iterations {
            let record = BorrowedSlotRecord(ref: BorrowedSlotRef(tag: tag), name: "iter", code: i)
            receiver.onRecord(record)
            last = describeBorrowedRecord(record)
        }
        return last
    }
}
