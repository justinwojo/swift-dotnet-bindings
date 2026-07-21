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

    /// Serial from the registry-aware allocation record, handed back at deinit so the live
    /// entry is dropped. Stored on the class (a heap ref) — the embedding structs hold only a
    /// pointer to this instance, so the extra field does not change any struct's wire layout.
    private let trackedSerial: Int64

    public init(tag: Int32, category: String = "TrackedRef") {
        self.tag = tag
        self.trackedSerial = recordTrackedAllocation(category: category, tag: tag)
    }

    deinit {
        recordTrackedDeallocation(serial: trackedSerial)
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
        self.ref = TrackedRef(tag: value, category: "NonFrozenStructWithRef")
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
        self.ref = TrackedRef(tag: value, category: "FrozenStructWithRef")
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
        self.a = TrackedRef(tag: value, category: "LargeFrozenStructWithRef")
        self.b = TrackedRef(tag: value, category: "LargeFrozenStructWithRef")
        self.c = TrackedRef(tag: value, category: "LargeFrozenStructWithRef")
        self.d = TrackedRef(tag: value, category: "LargeFrozenStructWithRef")
        self.e = TrackedRef(tag: value, category: "LargeFrozenStructWithRef")
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

// MARK: - Async Collection-Return Carrier Leak Probe Fixtures
//
// The factories above are SYNCHRONOUS — their generated C# returns the collection on the
// calling frame, where the collection-return cleanup already value-witness-Destroys the
// carrier. The ASYNC collection-return path is a SEPARATE generated callback (the async
// completion thunk reads the Swift-allocated carrier on a Swift continuation thread). The
// Swift async wrapper writes the result via `initializeMemory(as: <Container>.self)`, which
// runs the container's copy witness and takes a +1 on the copy-on-write storage. The C#
// completion callback marshals the container (a second, independent +1 via NewFromPayload's
// InitializeWithCopy) and then frees the carrier — so it must value-witness-Destroy the
// carrier's +1 first, or the entire backing storage (and every element/value/member it holds)
// leaks once per awaited call. Each fixture forces a real suspension so the C# foreground frame
// unwinds before the carrier is read, exactly like a real async collection return.

/// Async `[TrackedRef]` — array carrier holds a +1 on the CoW storage backing every element.
public func fetchTrackedRefArray(count: Int32) async -> [TrackedRef] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: [TrackedRef] = []
    for i in 0..<count {
        result.append(TrackedRef(tag: i))
    }
    return result
}

/// Async `[TrackedRefStruct]` — array of a NON-frozen (resilient) struct embedding a
/// `TrackedRef`. This is the `[ResilientStruct]` async-return shape: the array carrier's +1
/// pins the CoW storage holding every struct, and each struct's buffer holds the embedded ref.
public func fetchTrackedRefStructArray(count: Int32) async -> [TrackedRefStruct] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: [TrackedRefStruct] = []
    for i in 0..<count {
        result.append(TrackedRefStruct(value: i))
    }
    return result
}

/// Async `[Int32: TrackedRef]` — dictionary carrier holds a +1 on the CoW storage backing every value.
public func fetchTrackedRefDict(count: Int32) async -> [Int32: TrackedRef] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: [Int32: TrackedRef] = [:]
    for i in 0..<count {
        result[i] = TrackedRef(tag: i)
    }
    return result
}

/// Async `Set<TrackedRef>` — set carrier holds a +1 on the CoW storage backing every member.
public func fetchTrackedRefSet(count: Int32) async -> Set<TrackedRef> {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: Set<TrackedRef> = []
    for i in 0..<count {
        result.insert(TrackedRef(tag: i))
    }
    return result
}

// MARK: - Borrowed Callback-Arg Leak Probe (Finding 11 — the borrow leak, T1)
//
// The probes above exercise the *return* direction (a Copy-semantics wrapper handed
// BACK to C#). These two exercise the opposite direction: a Copy-semantics runtime
// wrapper passed BY VALUE *into* a C# callback. The generated C# reads the callback
// arg through the borrowed callback-arg marshal (`MarshalCallbackArg<…>`); the
// SwiftResult/SwiftArray from-handle ctor runs `NativeMemory.Alloc` +
// `InitializeWithCopy`, owning the native buffer plus a +1 on the embedded payload.
// The old blanket finalizer-suppression on the borrowed path foreclosed the wrapper's
// VWT Destroy and leaked that copy per invocation; the declared Copy semantics now keep
// the finalizer so the buffer + embedded ref are released. Each payload embeds a
// `LifetimeTracker`-counted `TrackedRef`, so a suppressed Destroy shows up as a non-zero
// live count after the callback loop and a GC drain — not merely "does not crash".

/// Passes a `Result<TrackedRef, TrackedRefError>` (the Copy-semantics SwiftResult wrapper)
/// BY VALUE into an escaping-shaped callback. Loop `count` times; the live count must return
/// to 0 once the borrowed SwiftResult wrappers finalize.
public func invokeWithBorrowedTrackedResult(count: Int32,
        _ body: (Result<TrackedRef, TrackedRefError>) -> Void) {
    for i in 0..<count { body(.success(TrackedRef(tag: i))) }
}

/// Companion SwiftArray Copy-wrapper shape: passes `[TrackedRef]` BY VALUE into a callback.
/// Same borrow-without-dispose leak class if the Copy wrapper's finalizer is suppressed.
public func invokeWithBorrowedTrackedArray(count: Int32, _ body: ([TrackedRef]) -> Void) {
    for i in 0..<count { body([TrackedRef(tag: i)]) }
}

/// Move-wrapper shape: passes a heap-form (non-small, >15 UTF-8 bytes) `String` BY VALUE into a
/// callback `count` times. The C# wrapper bitwise-copies the borrowed two-word String into a
/// container buffer it allocates itself, so per invocation it must (a) never value-witness-destroy
/// the borrowed String this loop still owns (over-release → corruption/crash) and (b) still free
/// its OWN container (the old blanket finalizer suppression leaked it per invocation).
public func invokeWithBorrowedString(count: Int32, _ body: (String) -> Void) {
    let payload = String(repeating: "borrowed-string-move-arm/", count: 4)
    for _ in 0..<count { body(payload) }
}

// MARK: - Extraction-Side Retain Probe (Optional `.Some` / Result `.Success` copy-out)

/// Strong global holding the SAME `TrackedRef` embedded in the struct handed back through the
/// Optional/Result wire carrier below. The C# `.Some` / `.Success` getter COPIES the payload out
/// of the carrier into a fresh wrapper that value-witness-destroys on Dispose. The source payload
/// — and this global — outlive that extraction, so the copy MUST take a value-witness retain. If
/// it under-retains, disposing the extracted wrapper over-releases the embedded ref and prematurely
/// deallocates an object this global still owns: observable as the live count dropping to 0 while
/// the global is non-nil (a dangling global pointer), with no GC timing involved.
private var _sharedExtractionRef: TrackedRef?

/// Builds a non-frozen `TrackedRefStruct` (SafeHandle copy-out path, same as String/Array/Dict
/// COW storage) whose `ref` field IS the `TrackedRef` stashed in the global, so the struct and the
/// global share one instance.
private func makeStructSharingGlobalRef(value: Int32) -> TrackedRefStruct {
    let shared = TrackedRef(tag: value)
    _sharedExtractionRef = shared
    var s = TrackedRefStruct(value: value)
    s.ref = shared
    return s
}

/// Stashes a `TrackedRef` in a global, then returns `Optional<TrackedRefStruct>` whose payload
/// embeds that SAME ref. Drives the `SwiftOptional<T>.Some` extraction copy.
public func stashSharedRefAndReturnOptionalStruct(value: Int32) -> TrackedRefStruct? {
    return makeStructSharingGlobalRef(value: value)
}

/// Stashes a `TrackedRef` in a global, then returns `Result<TrackedRefStruct, _>.success` whose
/// payload embeds that SAME ref. Drives the `SwiftResult.ExtractPayloadValue` (`.Success`) copy.
public func stashSharedRefAndReturnResultStruct(value: Int32) -> Result<TrackedRefStruct, TrackedRefError> {
    return .success(makeStructSharingGlobalRef(value: value))
}

/// Drops the global's strong reference established by the stash factories above, releasing the
/// last retain on the shared `TrackedRef`.
public func clearSharedExtractionRef() {
    _sharedExtractionRef = nil
}

/// Complex (payload-carrying) enum whose `.present` case holds a `TrackedRef`. Projects to the
/// ISwiftObject (non-`ISwiftStruct`) complex-enum path, whose `NewFromPayload` ADOPTS the wire
/// handle directly. Extraction (`.Some`/`.Success`) must therefore take a value-witness retain on
/// the embedded ref — the same under-retain shape as the non-frozen struct, but reached through the
/// enum projection rather than the struct projection.
public enum TrackedRefEnum {
    case empty
    case present(TrackedRef)
}

/// Stashes a `TrackedRef` in the shared global, then returns `Optional<TrackedRefEnum>.some(.present(ref))`
/// embedding that SAME ref. Drives the complex-enum branch of `SwiftOptional<T>.Some`.
public func stashSharedRefAndReturnOptionalEnum(value: Int32) -> TrackedRefEnum? {
    let shared = TrackedRef(tag: value)
    _sharedExtractionRef = shared
    return .present(shared)
}

/// Stashes a `TrackedRef` in the shared global, then returns `Result<TrackedRefEnum, _>.success(.present(ref))`
/// embedding that SAME ref. Drives the complex-enum branch of `SwiftResult.ExtractPayloadValue`.
public func stashSharedRefAndReturnResultEnum(value: Int32) -> Result<TrackedRefEnum, TrackedRefError> {
    let shared = TrackedRef(tag: value)
    _sharedExtractionRef = shared
    return .success(.present(shared))
}

/// Returns `Optional<String>` so the C# `SwiftOptional<SwiftString>.Some` extraction exercises the
/// MOVE-bitwise (`PayloadConstructionSemantics.Move`) NewFromPayload shape: SwiftString allocates its
/// own buffer and bitwise-copies the temporary, transferring the bridge-object retain. The
/// extraction must NOT value-witness-destroy the temporary (that would over-release the shared
/// string storage). A tight extract+dispose loop over this surfaces the over-release as a crash.
public func makeOptionalString(present: Bool, value: Int32) -> String? {
    return present ? "tracked-\(value)" : nil
}

/// `Result<String, _>` companion to `makeOptionalString`, driving the SwiftString MOVE shape through
/// `SwiftResult.ExtractPayloadValue` (`.Success`).
public func makeResultString(value: Int32) -> Result<String, TrackedRefError> {
    return .success("tracked-\(value)")
}

/// Frozen blittable POD struct. The generator projects this as a C# **value-type** `ISwiftObject`
/// (a `struct`, NOT `ISwiftStruct`): its `NewFromPayload` reads it by value via `*(T*)handle` and its
/// `SwiftHandle` is the throwing default (value structs are not heap-backed). Optional/Result
/// extraction of such a value reads it by value and frees the temporary buffer WITHOUT touching
/// `.SwiftHandle` during cleanup — this is the only payload kind that is `ISwiftObject` yet
/// read-by-value, so it guards the cleanup branch that must not compare a (throwing) handle.
@frozen
public struct ExtractionPodPoint {
    public var x: Int64
    public var y: Int64
    public init(x: Int64, y: Int64) { self.x = x; self.y = y }
}

/// `Optional<ExtractionPodPoint>` factory driving the value-type-struct branch of `SwiftOptional.Some`.
public func makeOptionalPodPoint(present: Bool, x: Int64, y: Int64) -> ExtractionPodPoint? {
    return present ? ExtractionPodPoint(x: x, y: y) : nil
}

/// `Result<ExtractionPodPoint, _>` companion driving the value-type-struct branch of
/// `SwiftResult.ExtractPayloadValue`.
public func makeResultPodPoint(x: Int64, y: Int64) -> Result<ExtractionPodPoint, TrackedRefError> {
    return .success(ExtractionPodPoint(x: x, y: y))
}

// MARK: - Dictionary / Set with ref-containing NON-class values (MOVE-context element move, P1)

/// Factory returning `[Int32: FrozenTrackedRefStruct]`. The dictionary VALUE is a frozen-with-ref
/// struct (the COPY ownership shape: NewFromPayload allocates its own buffer + InitializeWithCopy).
/// Enumerating / looking up a value moves it out of the iterator's `Optional<(K,V)>` buffer via
/// `MarshalMovedValueFromSlot`. Before the per-shape move fix, only true CLASS values were handled;
/// a frozen-with-ref struct value fell to a bitwise read + raw free with no value-witness Destroy of
/// the slot's `+1` — leaking one embedded `TrackedRef` per moved value.
public func makeFrozenRefValueDict(count: Int32) -> [Int32: FrozenTrackedRefStruct] {
    var result: [Int32: FrozenTrackedRefStruct] = [:]
    for i in 0..<count {
        result[i] = FrozenTrackedRefStruct(value: i)
    }
    return result
}

/// Factory returning `[Int32: TrackedRefStruct]`. The dictionary VALUE is a non-frozen ref struct
/// (the ADOPT ownership shape: NewFromPayload adopts the buffer pointer directly). Moving such a
/// value out of the iterator buffer with the old bitwise path adopted the slot itself, so freeing
/// the slot raw afterwards left the wrapper's SafeHandle dangling at freed memory — a use-after-free
/// on dispose, not merely a leak. The per-shape move copies into a stable buffer first.
public func makeNonFrozenRefValueDict(count: Int32) -> [Int32: TrackedRefStruct] {
    var result: [Int32: TrackedRefStruct] = [:]
    for i in 0..<count {
        result[i] = TrackedRefStruct(value: i)
    }
    return result
}

/// Frozen struct carrying a `TrackedRef`, made `Hashable` (by the ref's identity) so it can be a
/// Swift `Set` element. Projects to the COPY shape, exercising `SwiftSet.CollectElements`' per-shape
/// element move for a ref-containing non-class element.
@frozen
public struct HashableFrozenTrackedRefStruct: Hashable {
    public var ref: TrackedRef
    public var value: Int32

    public init(value: Int32) {
        self.ref = TrackedRef(tag: value)
        self.value = value
    }

    public static func == (lhs: HashableFrozenTrackedRefStruct, rhs: HashableFrozenTrackedRefStruct) -> Bool {
        lhs.ref === rhs.ref
    }
    public func hash(into hasher: inout Hasher) { hasher.combine(ObjectIdentifier(ref)) }
}

/// Factory returning `Set<HashableFrozenTrackedRefStruct>`. Each element embeds a unique `TrackedRef`
/// (identity hashing keeps them all distinct). Enumerating the set moves each element out of the
/// iterator buffer via `MarshalMovedValueFromSlot`; the per-shape move must value-witness-Destroy the
/// slot's `+1` after copying out, or each element's embedded ref leaks.
public func makeFrozenRefValueSet(count: Int32) -> Set<HashableFrozenTrackedRefStruct> {
    var result: Set<HashableFrozenTrackedRefStruct> = []
    for i in 0..<count {
        result.insert(HashableFrozenTrackedRefStruct(value: i))
    }
    return result
}

// MARK: - Optional / Result of a tuple embedding a class + heap String (COPY-context per-element, P3)

/// Stashes a `TrackedRef` in the shared global, then returns `Optional<(TrackedRef, String)>` whose
/// tuple embeds that SAME class ref alongside a heap-backed `String` (long enough to defeat the
/// ≤15-byte small-string inlining, forcing real heap storage). A tuple is not itself `ISwiftObject`,
/// so the carrier copy is a bitwise `+0` whole-tuple read; the per-element extraction
/// (`ExtractCopiedElement`) must take an INDEPENDENT `+1` on the class (deref + retain) and on the
/// String (InitializeWithCopy / MOVE) so disposing the extracted element wrappers never over-releases
/// the carrier's (and this global's) references. Under-retain surfaces as the global's live count
/// dropping to 0 while the global is non-nil, and as string-storage over-release on the heap String.
public func stashSharedRefAndReturnOptionalTuple(value: Int32) -> (TrackedRef, String)? {
    let shared = TrackedRef(tag: value)
    _sharedExtractionRef = shared
    return (shared, "tracked-tuple-\(value)-padding-well-past-the-fifteen-byte-inline-threshold")
}

/// `Result<(TrackedRef, String), _>.success` companion to `stashSharedRefAndReturnOptionalTuple`,
/// driving the same per-element tuple extraction through `SwiftResult.ExtractPayloadValue`.
public func stashSharedRefAndReturnResultTuple(value: Int32) -> Result<(TrackedRef, String), TrackedRefError> {
    let shared = TrackedRef(tag: value)
    _sharedExtractionRef = shared
    return .success((shared, "tracked-tuple-\(value)-padding-well-past-the-fifteen-byte-inline-threshold"))
}
