// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Borrowed-slot receiver parameters: the remaining copy-out carriers
//
// `BorrowedSlotParamDispatch.swift` covers the two families that motivated the borrowed copy-out
// (a non-frozen struct / associated-value enum, and a one-byte payload-free enum, plus Optionals
// of each). This file covers the carriers that share the same copy-out but had no end-to-end
// exercise: a `@frozen` struct that owns memory, `Foundation.Data`, a tuple mixing a scalar with a
// reference-backed element, a tuple of two wrapper-projected elements, and a `KeyPath`.
//
// The hazard is identical in every arm. Swift hands the receiver the ADDRESS of a slot it owns —
// it copies the argument into a local and deinitializes that local as soon as the receiver
// returns — while the C# carrier for each of these is a managed wrapper CLASS, not a blittable
// value. Reading the slot bitwise reinterprets Swift's first payload word as a managed object
// reference, so the copy must go through the value witness and take an independent +1 that leaves
// the source slot intact.
//
// Each driver deliberately re-reads the ORIGINAL after the callback returns and hands the caller a
// summary of it, so a receiver that consumed or destroyed the borrowed source surfaces as a
// corrupted summary rather than as silent luck. The tracked class payload feeds the shared
// LifetimeTracker counters (`recordTrackedAllocation` / `recordTrackedDeallocation`, defined in
// Lifetime/OwnershipTests.swift) so repeated dispatch can be asserted for ARC balance too.
//
// `Result` is NOT among the parameter arms here, and deliberately so: a `Result`-typed argument is
// return-only by design — the bound-generic gate drops any member that takes one — so a
// requirement declaring it would be silently dropped rather than dispatched. The `Result` carrier
// is exercised at the end of this file in the one reverse-dispatch position where it is legal: a
// getter requirement whose value travels C# -> Swift.

// MARK: - Payload types

/// Tracked class payload. Reaching it through a value handed back to Swift after the callback is
/// what proves a copy-out took a real reference rather than a reinterpreted word.
public final class ComplexSlotRef {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Non-frozen struct used as a `Result` success value - a class field plus a String field, so the
/// success payload has two independent references to get right.
public struct ComplexSlotPayload {
    public let ref: ComplexSlotRef
    public let label: String
    public let amount: Int32

    public init(ref: ComplexSlotRef, label: String, amount: Int32) {
        self.ref = ref
        self.label = label
        self.amount = amount
    }
}

/// Failure half of the `Result`. A payload case is included so the failure arm carries storage of
/// its own rather than being a bare discriminator.
public enum ComplexSlotFault: Error, Equatable {
    case refused
    case invalid(code: Int32)
}

/// `@frozen` struct that OWNS memory: the String field makes its layout fixed but not POD, so it
/// projects to a copying wrapper class rather than a blittable C# struct. That is the arm where a
/// bitwise read reinterprets the String's COW storage pointer as a managed reference.
@frozen
public struct FrozenSlotLabel {
    public let text: String
    public let weight: Int32

    public init(text: String, weight: Int32) {
        self.text = text
        self.weight = weight
    }
}

/// Payload-free enum used as the second element of the composite tuple, so that tuple mixes two
/// different wrapper-projected element kinds rather than repeating one.
public enum ComplexSlotTone {
    case warm
    case cool
}

// MARK: - Value readers
//
// Free functions rather than members so the C# side can describe a received value without
// depending on which members of a wrapper type the generator chooses to bind.

/// Renders a frozen-with-memory struct. Reading `text` dereferences the String storage the
/// borrowed slot owns, which is the operation that faults on a bitwise read.
public func describeFrozenSlotLabel(_ label: FrozenSlotLabel) -> String {
    return "\(label.text)@\(label.weight)"
}

public func describeOptionalFrozenSlotLabel(_ label: FrozenSlotLabel?) -> String {
    guard let label else { return "nil" }
    return describeFrozenSlotLabel(label)
}

/// Renders a `Data` as its byte count and byte sum, so both the length word and the actual bytes
/// have to have survived the copy-out.
public func describeSlotData(_ payload: Data) -> String {
    let sum = payload.reduce(Int64(0)) { $0 &+ Int64($1) }
    return "\(payload.count):\(sum)"
}

public func describeOptionalSlotData(_ payload: Data?) -> String {
    guard let payload else { return "nil" }
    return describeSlotData(payload)
}

public func describeComplexSlotTone(_ tone: ComplexSlotTone) -> String {
    switch tone {
    case .warm: return "warm"
    case .cool: return "cool"
    }
}

/// Renders a `Result`, reaching into the success payload's class field and String field, or into
/// the failure case's associated value. Used to describe a value that arrived FROM C#.
///
/// Deliberately `internal`, not `public`: a `Result`-typed ARGUMENT is return-only by design, so
/// exporting this would only add a skipped member to the binding surface. The C# side reaches it
/// through `ResultOutcomeDriver`, whose signature takes the conformer rather than the `Result`.
internal func describeComplexSlotResult(_ result: Result<ComplexSlotPayload, ComplexSlotFault>) -> String {
    switch result {
    case .success(let payload):
        return "ok/\(payload.label)#\(payload.amount)/\(payload.ref.tag)"
    case .failure(.refused):
        return "err/refused"
    case .failure(.invalid(let code)):
        return "err/invalid#\(code)"
    }
}

// MARK: - Reverse-dispatch protocol

/// Class-bound protocol whose requirements take one borrowed-slot carrier each. The generated
/// `IComplexSlotReceiver` is what the C# test implements; Swift calls back into it through the
/// generated proxy receiver.
public protocol ComplexSlotReceiver: AnyObject {
    /// `@frozen` struct owning a String - a copying wrapper class, not a blittable struct.
    func onFrozenLabel(_ label: FrozenSlotLabel)
    /// `Foundation.Data` - a Foundation value wrapper.
    func onData(_ payload: Data)
    /// Tuple mixing a scalar with a reference-backed element: bitwise-unreadable because of the
    /// String, so the whole tuple goes through the runtime element walk.
    func onScalarTuple(_ pair: (Int32, String))
    /// Tuple of two wrapper-projected elements, so neither element can carry the read on its own.
    func onCompositeTuple(_ pair: (FrozenSlotLabel, ComplexSlotTone))
    /// Optional of the frozen-with-memory struct.
    func onOptionalFrozenLabel(_ label: FrozenSlotLabel?)
    /// Optional of the Foundation value wrapper.
    func onOptionalData(_ payload: Data?)
}

/// Synchronous driver: builds values Swift-side, dispatches them on the same thread (so the C#
/// test needs no runloop pumping), then re-reads the ORIGINALS and returns a summary of them.
public final class ComplexSlotDriver {
    public init() {}

    /// Dispatches a frozen-with-memory struct, then describes the original.
    public func driveFrozenLabel(_ receiver: ComplexSlotReceiver, text: String, weight: Int32) -> String {
        let label = FrozenSlotLabel(text: text, weight: weight)
        receiver.onFrozenLabel(label)
        return describeFrozenSlotLabel(label)
    }

    /// Dispatches `Data` built from `0..<count`, then describes the original. Building it Swift-side
    /// keeps the fixture off the `[UInt8]` parameter path, which is not what is under test here.
    public func driveData(_ receiver: ComplexSlotReceiver, count: Int32) -> String {
        let payload = Data((0..<max(0, Int(count))).map { UInt8($0 % 251) })
        receiver.onData(payload)
        return describeSlotData(payload)
    }

    /// Dispatches a `(Int32, String)` tuple, then describes the original.
    public func driveScalarTuple(_ receiver: ComplexSlotReceiver, number: Int32, text: String) -> String {
        let pair = (number, text)
        receiver.onScalarTuple(pair)
        return "\(pair.0)/\(pair.1)"
    }

    /// Dispatches a `(FrozenSlotLabel, ComplexSlotTone)` tuple, then describes the original.
    public func driveCompositeTuple(_ receiver: ComplexSlotReceiver, text: String, weight: Int32, warm: Bool) -> String {
        let pair = (FrozenSlotLabel(text: text, weight: weight), warm ? ComplexSlotTone.warm : ComplexSlotTone.cool)
        receiver.onCompositeTuple(pair)
        return describeFrozenSlotLabel(pair.0) + "/" + describeComplexSlotTone(pair.1)
    }

    /// Dispatches a non-nil Optional frozen struct, then describes the original.
    public func driveOptionalFrozenLabel(_ receiver: ComplexSlotReceiver, text: String, weight: Int32) -> String {
        let label: FrozenSlotLabel? = FrozenSlotLabel(text: text, weight: weight)
        receiver.onOptionalFrozenLabel(label)
        return describeOptionalFrozenSlotLabel(label)
    }

    /// Dispatches a nil Optional frozen struct - the case that must not fault on the tag read.
    public func driveNilFrozenLabel(_ receiver: ComplexSlotReceiver) -> String {
        receiver.onOptionalFrozenLabel(nil)
        return "nil"
    }

    /// Dispatches a non-nil Optional `Data`, then describes the original.
    public func driveOptionalData(_ receiver: ComplexSlotReceiver, count: Int32) -> String {
        let payload: Data? = Data((0..<max(0, Int(count))).map { UInt8($0 % 251) })
        receiver.onOptionalData(payload)
        return describeOptionalSlotData(payload)
    }

    /// Dispatches a nil Optional `Data`.
    public func driveNilData(_ receiver: ComplexSlotReceiver) -> String {
        receiver.onOptionalData(nil)
        return "nil"
    }

    /// Repeats the frozen-with-memory dispatch. The wrapper for a copy-semantics frozen struct
    /// allocates its own buffer and value-witness-copies into it, so an unbalanced temporary here
    /// leaks the String storage. Returning the original's description on every iteration means a
    /// copy-out that destroys the borrowed source shows up as garbage rather than as a slow leak.
    public func driveFrozenLabelRepeatedly(_ receiver: ComplexSlotReceiver, iterations: Int32, text: String) -> String {
        var last = ""
        for i in 0..<iterations {
            let label = FrozenSlotLabel(text: text, weight: i)
            receiver.onFrozenLabel(label)
            last = describeFrozenSlotLabel(label)
        }
        return last
    }

    /// Repeats the `Data` dispatch, for the same reason: `Foundation.Data` owns a heap buffer, so
    /// an unbalanced copy-out here either strands one buffer per iteration or frees a buffer the
    /// original still points at.
    public func driveDataRepeatedly(_ receiver: ComplexSlotReceiver, iterations: Int32, count: Int32) -> String {
        var last = ""
        let payload = Data((0..<max(0, Int(count))).map { UInt8($0 % 251) })
        for _ in 0..<iterations {
            receiver.onData(payload)
            last = describeSlotData(payload)
        }
        return last
    }
}

// MARK: - Tracked-payload leak probe
//
// The arms above own heap storage that LifetimeTracker cannot count (String COW buffers, Data
// backing stores). This protocol carries a tracked CLASS inside the borrowed value so repeated
// dispatch can be asserted against an exact live-object count: over-retaining strands one object
// per iteration, under-retaining drops the count below the surviving owner.

/// Non-frozen struct wrapping the tracked class, so the borrowed slot holds a reference the
/// tracker can see.
public struct TrackedSlotBox {
    public let ref: ComplexSlotRef
    public let note: String

    public init(ref: ComplexSlotRef, note: String) {
        self.ref = ref
        self.note = note
    }
}

public func describeTrackedSlotBox(_ box: TrackedSlotBox) -> String {
    return "\(box.note)#\(box.ref.tag)"
}

public protocol TrackedSlotReceiver: AnyObject {
    func onTrackedBox(_ box: TrackedSlotBox)
}

public final class TrackedSlotDriver {
    public init() {}

    /// Dispatches one tracked box held for the whole call, then describes the ORIGINAL.
    public func driveTrackedBox(_ receiver: TrackedSlotReceiver, tag: Int32, note: String) -> String {
        let box = TrackedSlotBox(ref: ComplexSlotRef(tag: tag), note: note)
        receiver.onTrackedBox(box)
        return describeTrackedSlotBox(box)
    }

    /// Dispatches the SAME box `iterations` times. Exactly one tracked object exists for the whole
    /// loop, so any per-iteration imbalance in the copy-out is visible as a live count other than
    /// 1 at the end of the loop.
    public func driveTrackedBoxRepeatedly(_ receiver: TrackedSlotReceiver, iterations: Int32, tag: Int32) -> String {
        let box = TrackedSlotBox(ref: ComplexSlotRef(tag: tag), note: "loop")
        for _ in 0..<iterations {
            receiver.onTrackedBox(box)
        }
        return describeTrackedSlotBox(box)
    }
}

// MARK: - KeyPath receiver parameter
//
// Held apart from `ComplexSlotReceiver` on purpose. A KeyPath is a Swift CLASS, so the borrowed
// copy-out takes its class fast path (dereference the slot's instance pointer and retain it)
// rather than the value-witness extraction every other arm above uses. Isolating it keeps the two
// ownership shapes independently observable, and lets the KeyPath arm be run on its own.

/// KeyPath Root: a plain struct with one String and one scalar stored property.
public struct ComplexSlotBag {
    public var title: String
    public var count: Int32

    public init(title: String, count: Int32) {
        self.title = title
        self.count = count
    }
}

/// Reads a bag through a key path. The C# side calls this with the KeyPath it received, which is
/// what proves the copy-out produced a usable key-path object rather than a reinterpreted word.
public func readComplexSlotBagTitle(_ bag: ComplexSlotBag, keyPath: KeyPath<ComplexSlotBag, String>) -> String {
    return bag[keyPath: keyPath]
}

public protocol KeyPathSlotReceiver: AnyObject {
    /// A `KeyPath` value arriving in a borrowed slot.
    func onTitleKeyPath(_ keyPath: KeyPath<ComplexSlotBag, String>)
}

public final class KeyPathSlotDriver {
    public init() {}

    /// Dispatches `\ComplexSlotBag.title`, then reads a bag through the ORIGINAL key path.
    public func driveTitleKeyPath(_ receiver: KeyPathSlotReceiver, title: String) -> String {
        let keyPath = \ComplexSlotBag.title
        receiver.onTitleKeyPath(keyPath)
        return ComplexSlotBag(title: title, count: 0)[keyPath: keyPath]
    }
}

// MARK: - Result in the one reverse-dispatch position where it is legal
//
// A `Result` argument is return-only: the bound-generic gate drops any member that takes one, so
// there is no receiver-PARAMETER arm to exercise. A getter requirement travels the other way -
// C# produces the `Result` and Swift reads it - which is the reverse-dispatch path a `Result`
// carrier can actually reach.

/// Builds a Swift-originated success `Result` for the C# conformer to hand back.
///
/// The conformer needs one of these because there is no C#-side way to build a `Result` that
/// carries a native Swift payload: the managed `FromSuccess` / `FromFailure` factories produce
/// C#-only values that deliberately refuse to marshal INTO Swift. A conformer returning a
/// `Result` therefore has to be returning one it received from Swift.
public func makeComplexSlotSuccess(label: String, amount: Int32, tag: Int32) -> Result<ComplexSlotPayload, ComplexSlotFault> {
    return .success(ComplexSlotPayload(ref: ComplexSlotRef(tag: tag), label: label, amount: amount))
}

/// Builds a Swift-originated failure `Result` carrying an associated value.
public func makeComplexSlotFailure(code: Int32) -> Result<ComplexSlotPayload, ComplexSlotFault> {
    return .failure(.invalid(code: code))
}

public protocol ResultOutcomeReceiver: AnyObject {
    /// Read by Swift through the witness table; the value is produced on the C# side.
    var outcome: Result<ComplexSlotPayload, ComplexSlotFault> { get }
}

public final class ResultOutcomeDriver {
    public init() {}

    /// Reads the conformer's `outcome` and renders it Swift-side.
    public func describeOutcome(_ receiver: ResultOutcomeReceiver) -> String {
        return describeComplexSlotResult(receiver.outcome)
    }

    /// Reads it `iterations` times, so an unbalanced hand-off of the returned `Result` shows up as
    /// growth rather than as a single lucky read.
    public func describeOutcomeRepeatedly(_ receiver: ResultOutcomeReceiver, iterations: Int32) -> String {
        var last = ""
        for _ in 0..<iterations {
            last = describeComplexSlotResult(receiver.outcome)
        }
        return last
    }
}
