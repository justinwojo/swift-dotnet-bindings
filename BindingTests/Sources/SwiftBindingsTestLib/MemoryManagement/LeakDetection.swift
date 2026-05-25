// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Migrated from FunctionalTests/MemoryTests/MemoryTests.swift.
// Tests deinit tracking and struct-with-ref-field patterns that exercise
// the generator's Buffer vs SafeHandle emission paths.

// MARK: - Deinit Tracking

/// Reference type that tracks deinitialization via an unsafe pointer.
/// When the object is deallocated, `deinit` sets the pointee to 1,
/// allowing tests to verify that ARC cleanup occurred.
public class DeinitTracker {
    public var test: UnsafeMutablePointer<Int64>

    public init(test: UnsafeMutablePointer<Int64>) {
        self.test = test
    }

    deinit {
        test.pointee = 1
    }
}

// MARK: - Non-Frozen Struct with Ref at Offset 0

/// Non-frozen struct containing a reference type at offset 0.
/// The binding generator must emit ClassWithOpaquePayload (SafeHandle)
/// and properly invoke InitWithCopy/Destroy value witness functions.
public struct StructWithRefAtZero {
    public var refType: DeinitTracker
    private var refTypeTestPtr: UnsafeMutablePointer<Int64>

    public init() {
        refTypeTestPtr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTestPtr.initialize(to: 0)
        refType = DeinitTracker(test: refTypeTestPtr)
    }

    public init(refType: DeinitTracker) {
        self.refType = refType
        refTypeTestPtr = refType.test
    }

    public var refTypeTest: Int64 {
        get { return refTypeTestPtr.pointee }
    }

    public func cleanup() {
        refTypeTestPtr.deinitialize(count: 1)
        refTypeTestPtr.deallocate()
    }
}

// MARK: - Nested Non-Frozen Struct with Refs at Offsets 0, 16

/// Non-frozen struct with reference types at offsets 0 and 16.
/// Tests nested value witness operations across multiple ref fields.
public struct NestedStructWithRefs {
    public var refType: DeinitTracker
    private var refTypeTest1Ptr: UnsafeMutablePointer<Int64>
    public var inner: StructWithRefAtZero

    public init() {
        refTypeTest1Ptr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTest1Ptr.initialize(to: 0)
        refType = DeinitTracker(test: refTypeTest1Ptr)

        let refTypeTest2Ptr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTest2Ptr.initialize(to: 0)
        inner = StructWithRefAtZero(refType: DeinitTracker(test: refTypeTest2Ptr))
    }

    public init(refType1: DeinitTracker, refType2: DeinitTracker) {
        refTypeTest1Ptr = refType1.test
        refType = refType1
        inner = StructWithRefAtZero(refType: refType2)
    }

    public var refTypeTest1: Int64 {
        get { return refTypeTest1Ptr.pointee }
    }

    public var refTypeTest2: Int64 {
        get { return inner.refTypeTest }
    }

    public func cleanup() {
        inner.cleanup()
        refTypeTest1Ptr.deinitialize(count: 1)
        refTypeTest1Ptr.deallocate()
    }
}

// MARK: - Frozen Struct with Ref Field (ClassWithBufferStruct emission)

/// Frozen struct containing a reference type field.
/// The binding generator must emit ClassWithBufferStruct — a C# class
/// wrapping a Buffer inner struct that includes the ref field as IntPtr.
@frozen
public struct FrozenStructWithRef {
    public var a: DeinitTracker
    public var b: Int32

    public init(b: Int32) {
        self.a = DeinitTracker(test: UnsafeMutablePointer<Int64>.allocate(capacity: 1))
        self.b = b
    }

    public func getValue() -> Int32 {
        return b
    }

    public func callDispose(callback: @escaping () -> Void) {
        callback()
    }
}

// MARK: - Nested Frozen Struct with Ref

/// Frozen struct nesting another frozen struct that contains a ref field.
/// Tests that ClassWithBufferStruct emission propagates through nesting.
@frozen
public struct NestedFrozenStructWithRef {
    public var a: FrozenStructWithRef
    public var b: Int32

    public init(b: Int32) {
        self.a = FrozenStructWithRef(b: b)
        self.b = b
    }

    public func getValue() -> Int32 {
        return b
    }
}

// MARK: - Inner Layout Helper

/// Simple frozen struct with two primitive fields, used as a layout component
/// in EmbeddedStructWithRefAtOffset.
@frozen
public struct InnerFrozenLayout {
    public var x: Int32
    public var y: UInt8

    public init() {
        self.x = 1
        self.y = 2
    }
}

// MARK: - Embedded Struct with Ref at Non-Zero Offset

/// Frozen struct where the reference type field is at offset 8 (after
/// InnerFrozenLayout which occupies bytes 0-7). Tests that the binding
/// generator correctly handles ref fields at non-zero offsets.
@frozen
public struct EmbeddedStructWithRefAtOffset {
    public var x: InnerFrozenLayout
    public var y: UInt8
    public var z: DeinitTracker // offset is 8

    public init() {
        self.x = InnerFrozenLayout()
        self.y = 3
        self.z = DeinitTracker(test: UnsafeMutablePointer<Int64>.allocate(capacity: 1))
    }
}

// MARK: - Pass-Through Functions

/// Pass-through for frozen struct with ref field (ClassWithBufferStruct path).
public func passThroughFrozenWithRef(a: FrozenStructWithRef) -> FrozenStructWithRef {
    return a
}

/// Pass-through for nested frozen struct with ref field.
public func passThroughNestedFrozenWithRef(a: NestedFrozenStructWithRef) -> NestedFrozenStructWithRef {
    return a
}

/// Pass-through for non-frozen struct with ref field (SafeHandle path).
public func passThroughNonFrozenWithRef(a: StructWithRefAtZero) -> StructWithRefAtZero {
    return a
}

/// Pass-through for embedded struct with ref at non-zero offset.
public func passThroughEmbeddedStruct(a: EmbeddedStructWithRefAtOffset) -> EmbeddedStructWithRefAtOffset {
    return a
}

/// Generic pass-through function.
public func passThroughGenericValue<T>(a: T) -> T {
    return a
}

// MARK: - Counter-Tracked Struct-With-Ref Fixtures (VWT Destroy on GC)

/// Reference type that participates in the shared allocation counters defined in
/// Lifetime/OwnershipTests.swift (the same counters `LifetimeTracker` reads).
///
/// Unlike `DeinitTracker`, it owns no external probe buffer, so instances can be
/// churned through tight create-and-abandon leak loops without leaking a side
/// allocation per instance. Embedding it in the struct fixtures below lets a
/// leak test assert that the GC finalizer actually drove VWT Destroy — which
/// ARC-releases this ref and decrements the live count back to zero.
public final class TrackedRef: Hashable {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }

    // Identity-based Hashable so the type can be an element of a Swift Set
    // (the SwiftSet copy-out leak probe). Conformance is additive and does not
    // affect the existing struct/array/optional fixtures.
    public static func == (lhs: TrackedRef, rhs: TrackedRef) -> Bool { lhs === rhs }
    public func hash(into hasher: inout Hasher) { hasher.combine(ObjectIdentifier(self)) }
}

/// Error carrier for the `SwiftResult` copy-out leak probe.
public enum TrackedRefError: Error {
    case failed
}

/// Non-frozen struct carrying a `TrackedRef`. Projects to the
/// ClassWithOpaquePayload (SafeHandle) path — disposal/finalization runs VWT
/// Destroy on the buffer, which ARC-releases the embedded `TrackedRef`.
public struct TrackedRefStruct {
    public var ref: TrackedRef
    public var value: Int32

    public init(value: Int32) {
        self.ref = TrackedRef(tag: value)
        self.value = value
    }
}

/// Frozen struct carrying a `TrackedRef` field. Projects to the
/// ClassWithBufferStruct path — a C# class wrapping a Buffer inner struct whose
/// VWT Destroy (on dispose or finalize) ARC-releases the embedded `TrackedRef`.
@frozen
public struct FrozenTrackedRefStruct {
    public var ref: TrackedRef
    public var value: Int32

    public init(value: Int32) {
        self.ref = TrackedRef(tag: value)
        self.value = value
    }
}

/// Factory for the non-frozen tracked struct.
public func makeTrackedRefStruct(value: Int32) -> TrackedRefStruct {
    return TrackedRefStruct(value: value)
}

/// Factory for the frozen tracked struct.
public func makeFrozenTrackedRefStruct(value: Int32) -> FrozenTrackedRefStruct {
    return FrozenTrackedRefStruct(value: value)
}

/// Pass-through (round-trip) for the non-frozen tracked struct.
public func passThroughTrackedRefStruct(_ a: TrackedRefStruct) -> TrackedRefStruct {
    return a
}

/// Pass-through (round-trip) for the frozen tracked struct.
public func passThroughFrozenTrackedRefStruct(_ a: FrozenTrackedRefStruct) -> FrozenTrackedRefStruct {
    return a
}

/// Large frozen struct carrying FIVE `TrackedRef` fields (5 × 8 = 40 bytes,
/// exceeding the 4-GPR / 32-byte arm64 direct-return threshold). Where the small
/// `FrozenTrackedRefStruct` returns by value in registers (the "Direct" return
/// strategy), this one is returned through an indirect result buffer (the
/// "IndirectResult" strategy) — the callee initializes the struct INTO a heap
/// buffer the caller allocates. It still projects to the ClassWithBufferStruct
/// path, so NewFromPayload COPIES out of that buffer. This fixture exists to prove
/// the indirect-result success-path cleanup VWT-destroys the temp buffer's retains
/// (one per embedded `TrackedRef`) before freeing it, rather than leaking them.
@frozen
public struct LargeFrozenTrackedRefStruct {
    public var a: TrackedRef
    public var b: TrackedRef
    public var c: TrackedRef
    public var d: TrackedRef
    public var e: TrackedRef

    public init(value: Int32) {
        self.a = TrackedRef(tag: value)
        self.b = TrackedRef(tag: value)
        self.c = TrackedRef(tag: value)
        self.d = TrackedRef(tag: value)
        self.e = TrackedRef(tag: value)
    }
}

/// Factory for the large frozen tracked struct — exercises the IndirectResult
/// return path (struct exceeds the arm64 direct-return register budget).
public func makeLargeFrozenTrackedRefStruct(value: Int32) -> LargeFrozenTrackedRefStruct {
    return LargeFrozenTrackedRefStruct(value: value)
}

/// Pass-through (round-trip) for the large frozen tracked struct.
public func passThroughLargeFrozenTrackedRefStruct(_ a: LargeFrozenTrackedRefStruct) -> LargeFrozenTrackedRefStruct {
    return a
}

// MARK: - Wire-Carrier Copy-Out Probe Fixtures (Optional / Array of tracked refs)

/// Factory returning an `Optional<FrozenTrackedRefStruct>`. The wire carrier is a
/// `SwiftOptional<…>` value whose non-POD `NewFromPayload` runs InitializeWithCopy
/// (SwiftOptional.cs) — it COPIES the payload out of the result buffer, taking a +1
/// on the embedded `TrackedRef`. If the result-buffer cleanup only frees (without a
/// value-witness Destroy of the carrier), that +1 is orphaned: a per-call leak of
/// the embedded ref. `present: false` returns nil (no embedded ref → no leak either
/// way) so the test can contrast the two tags.
public func makeOptionalFrozenTrackedRefStruct(present: Bool, value: Int32) -> FrozenTrackedRefStruct? {
    return present ? FrozenTrackedRefStruct(value: value) : nil
}

/// Factory returning an `Optional<LargeFrozenTrackedRefStruct>`. The 5-ref payload (40 bytes)
/// exceeds the arm64 direct-return register budget, so the Optional is returned via the
/// IndirectResult strategy — the @_cdecl wrapper writes the `Optional<T>` value into a heap
/// result buffer and the marshaller copies it out (VWT InitializeWithCopy, +1 on all 5 embedded
/// refs). Unlike the small-Optional probe (which returns by-value in registers), this exercises
/// the IndirectResult copy-out arm: if that arm doesn't value-witness-destroy the source buffer,
/// all 5 embedded refs leak per call.
public func makeOptionalLargeFrozenTrackedRefStruct(present: Bool, value: Int32) -> LargeFrozenTrackedRefStruct? {
    return present ? LargeFrozenTrackedRefStruct(value: value) : nil
}

/// Factory returning `[TrackedRef]` — a Swift Array whose copy-on-write storage holds
/// `count` `TrackedRef` references. The wire carrier is a `SwiftArray<…>` value whose
/// `NewFromPayload` runs InitializeWithCopy (SwiftArray.cs), taking a +1 on the CoW
/// storage. If the result-buffer cleanup only frees the buffer without a value-witness
/// Destroy of the array carrier, that +1 is orphaned and the entire storage (all
/// `count` `TrackedRef`s) leaks per call.
public func makeTrackedRefArray(count: Int32) -> [TrackedRef] {
    var result: [TrackedRef] = []
    for i in 0..<count {
        result.append(TrackedRef(tag: i))
    }
    return result
}

/// Factory returning `[Int32: TrackedRef]` — wire carrier is SwiftDictionary, whose
/// from-handle constructor runs VWT InitializeWithCopy (SwiftDictionary.cs), taking a
/// +1 on the CoW storage that holds every value's reference.
public func makeTrackedRefDict(count: Int32) -> [Int32: TrackedRef] {
    var result: [Int32: TrackedRef] = [:]
    for i in 0..<count {
        result[i] = TrackedRef(tag: i)
    }
    return result
}

/// Factory returning `Set<TrackedRef>` — wire carrier is SwiftSet, whose from-handle
/// constructor runs VWT InitializeWithCopy (SwiftSet.cs), taking a +1 on the CoW storage
/// that holds every member's reference.
public func makeTrackedRefSet(count: Int32) -> Set<TrackedRef> {
    var result: Set<TrackedRef> = []
    for i in 0..<count {
        result.insert(TrackedRef(tag: i))
    }
    return result
}

/// Factory returning `Result<TrackedRef, TrackedRefError>` — wire carrier is SwiftResult,
/// whose from-handle constructor runs VWT InitializeWithCopy (SwiftResult.cs), taking a +1
/// on the success payload's embedded reference.
public func makeTrackedRefResult(success: Bool, value: Int32) -> Result<TrackedRef, TrackedRefError> {
    return success ? .success(TrackedRef(tag: value)) : .failure(.failed)
}
