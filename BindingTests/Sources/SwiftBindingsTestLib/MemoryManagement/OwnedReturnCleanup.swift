// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Owned indirect-result returns
//
// A Swift function that returns an address-only value writes it into a caller-allocated
// indirect-result buffer at +1: the caller owns both the storage AND the retains the value
// holds on its heap fields. Balancing that is the caller's job, and how it balances depends
// on what the managed carrier's NewFromPayload did with the buffer — adopt it, copy out of
// it, or read it by value. These fixtures give each of those a deinit-counted payload so a
// leak surfaces as an accounting residual (a non-zero LifetimeTracker live count) instead of
// only as "does not crash": a single correct round trip cannot tell an orphaned +1 from a
// balanced one.
//
// Coverage here is deliberately per-member-shape, because the indirect-result cleanup is
// emitted per member: an instance method, a static method and a property getter each build
// their own plan, on both a class parent and a struct parent, plus the concrete-specialization
// (CSM) emitter's separate return path.

/// Class parent producing the non-frozen `TrackedRefStruct` (a struct whose only field is a
/// `TrackedRef`, so the buffer carries exactly one +1 per call) through each member shape.
public final class OwnedReturnClassFactory {
    private var counter: Int32 = 0

    public init() {}

    /// Instance method — the ordinary `@_cdecl` indirect-result path.
    public func makeBox(_ tag: Int32) -> TrackedRefStruct {
        return TrackedRefStruct(value: tag)
    }

    /// Static method — same path, no `self` in the wrapper signature.
    public static func makeBoxStatic(_ tag: Int32) -> TrackedRefStruct {
        return TrackedRefStruct(value: tag)
    }

    /// Property getter — the cdecl-property wrapper arm, which builds its indirect-result
    /// cleanup separately from the method arm.
    public var boxProperty: TrackedRefStruct {
        return TrackedRefStruct(value: 7)
    }

    /// Throwing producer. On the throwing exit Swift never initializes the indirect result,
    /// so the buffer still holds whatever the allocator handed back — a cleanup that
    /// value-witness-destroys unconditionally would dereference those bytes. The success arm
    /// must still balance.
    public func makeBoxOrThrow(_ tag: Int32, shouldThrow: Bool) throws -> TrackedRefStruct {
        if shouldThrow {
            throw TrackedRefError.failed
        }
        return TrackedRefStruct(value: tag)
    }
}

/// Struct parent producing the same payload — the wrapper takes `self` by an opaque payload
/// pointer rather than a class reference, so it is a separate emission path.
public struct OwnedReturnStructFactory {
    public var seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    public func makeBox(_ tag: Int32) -> TrackedRefStruct {
        return TrackedRefStruct(value: tag &+ seed)
    }

    public var boxProperty: TrackedRefStruct {
        return TrackedRefStruct(value: seed)
    }
}

// MARK: - Owned `Foundation.Data` returns
//
// `Data` marshals as an inline (by-value) C# struct: the seam reads the bytes out of the
// indirect-result buffer and converts them to a managed `byte[]`, then frees the buffer's
// storage. Freeing the storage does NOT release the Swift value that was written into it —
// for any payload past `Data`'s inline threshold, the bytes live in a separate heap
// allocation the buffer holds the only reference to, so the allocation outlives every call.
//
// The allocation is made observable by handing `Data` a `.custom` deallocator that records
// into the same tracked-object counters `LifetimeTracker` reads: the deallocator runs exactly
// when the last reference to the storage goes away, so a leaked buffer value shows up as a
// live count that never returns to zero.

/// Byte count for the tracked payloads: comfortably past the threshold below which `Data`
/// stores bytes inline in the struct (where there is no separate allocation to leak).
let ownedDataPayloadByteCount: Int = 64 * 1024

/// Exposes the payload size to the managed probe as a function, so the probe asserts the
/// returned `byte[]` length against the same constant the fixture allocates rather than a
/// copy of the number.
public func trackedDataPayloadByteCount() -> Int {
    return ownedDataPayloadByteCount
}

private func makeTrackedData(_ byteCount: Int, tag: Int32) -> Data {
    let bytes = UnsafeMutableRawPointer.allocate(byteCount: byteCount, alignment: 1)
    bytes.initializeMemory(as: UInt8.self, repeating: UInt8(truncatingIfNeeded: tag), count: byteCount)
    let serial = recordTrackedAllocation(category: "OwnedDataStorage", tag: tag)
    return Data(
        bytesNoCopy: bytes,
        count: byteCount,
        deallocator: .custom { pointer, _ in
            pointer.deallocate()
            recordTrackedDeallocation(serial: serial)
        })
}

/// Protocol whose conformers drive the concrete-specialization (CSM) emitter. The generic
/// method below is specialized once per conformer, and each specialization emits its own
/// return-value marshalling — a separate code path from the `@_cdecl` wrapper the plain
/// functions use.
public protocol OwnedDataSeed {
    var seedTag: Int32 { get }
}

public struct OwnedDataSeedA: OwnedDataSeed {
    public var seedTag: Int32
    public init(seedTag: Int32) { self.seedTag = seedTag }
}

public struct OwnedDataSeedB: OwnedDataSeed {
    public var seedTag: Int32
    public init(seedTag: Int32) { self.seedTag = seedTag }
}

/// Vault whose generic member returns an owned `Data` with an out-of-line payload. Mirrors
/// the shape of an AEAD `open`/`seal` that hands back plaintext as `Data`.
public struct OwnedDataVault {
    public init() {}

    /// Concrete-specialization return path: `-> Data` projects to `byte[]`, so the Swift value
    /// is fully consumed inside the seam and the buffer can be released there.
    public func produce<S: OwnedDataSeed>(_ seed: S) -> Data {
        return makeTrackedData(ownedDataPayloadByteCount, tag: seed.seedTag)
    }
}

/// Non-generic producer on the ordinary `@_cdecl` path: the emitted body applies the projection
/// (`…ToByteArray()`) inside the call, so the Swift value is consumed there and its buffer can be
/// released before the wrapper returns — the counterpart to the CSM member above.
public func makeOwnedTrackedData(tag: Int32) -> Data {
    return makeTrackedData(ownedDataPayloadByteCount, tag: tag)
}

/// Accessor seam on the same carrier, and the opposite ownership answer. A property emits a private
/// getter that hands the RAW `Data` back and a public property that projects it, so the value is read
/// AFTER the getter's cleanup has run. Releasing the payload in that cleanup would be a use-after-free
/// rather than a leak, so this fixture exists to make the getter's restraint observable: the bytes a
/// caller reads must still be the ones the fixture wrote.
public struct OwnedDataAccessorBox {
    private let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }

    public var trackedBytes: Data {
        return makeTrackedData(ownedDataPayloadByteCount, tag: tag)
    }
}
