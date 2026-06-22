// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Suppressed-proxy async fault-path carrier leak probe fixtures
//
// `Boxable` (Protocols/SuppressedProxyChannels.swift) is a plain protocol with an
// `init()` requirement, so EveryProtocol cannot conform and `BoxableProxy` is
// suppressed. An async function returning a bare `any Boxable` (or a container of
// them) therefore CANNOT marshal its result — the generated completion callback
// faults the awaiting Task with NotSupportedException. But the Swift async wrapper
// has ALREADY written the result into the carrier via
// `initializeMemory(as: <Existential>.self, repeating: result, count: 1)`, taking a
// value-witness +1 on the payload. Unless the completion callback releases that
// carrier +1 on the fault path BEFORE `SBW_Free` reclaims the raw allocation, the
// payload's retain is orphaned — a per-call leak.
//
// The existing `Boxable` conformer `BoxableIntCell` is a trivial value struct (no
// embedded references), so its existential destroy is a no-op and a leaked carrier
// produces NO measurable counter delta. These probes instead vend conformers that
// embed a `LifetimeTracker`-counted `TrackedRef` (LeakDetection.swift), so an
// orphaned carrier +1 shows up as a non-zero live count after the awaiting calls
// fault and the GC drains — not merely "does not crash". Because every one of these
// async members always faults (the proxy is suppressed), the probe is deterministic:
// RED before the carrier-release fix (the embedded refs pin), GREEN after.
//
// Arms covered (one fixture each):
//   * scalar OPAQUE existential `any Boxable`            → ExistentialContainer1 VWT Destroy
//   * collection `[any Boxable]`                          → SwiftArray<ExistentialContainer1> VWT Destroy
//   * dictionary `[Int32: any Boxable]`                   → SwiftDictionary<…> VWT Destroy (same arm as array)
//   * optional-collection `[any Boxable]?`                → already-shipped arm, regression guard
//   * scalar CLASS-BOUND existential `any …: AnyObject`  → ClassExistentialContainer1 → unknownObjectRelease
//
// A `Set<any Boxable>` twin is intentionally absent: `Boxable` is not `Hashable`, so a set of it
// cannot be formed. The array and dictionary fixtures both exercise the shared collection arm
// (`BuildCollectionCarrierMarshalLines` drives Array, Set, and Dictionary suppressed returns through
// the same `SwiftObjectHelper<carrier>.GetTypeMetadata()` + value-witness Destroy path).

// MARK: Tracked conformer for the OPAQUE `any Boxable` arms

/// A `final class` conformer of the suppressed-proxy `Boxable` protocol that embeds a
/// `LifetimeTracker`-counted `TrackedRef`. Stored inside an opaque `any Boxable`
/// existential, the single class reference lives inline in the container's buffer, and
/// the existential's value-witness destroy releases it — deallocating this cell and
/// deiniting its embedded `TrackedRef`. A leaked carrier pins the cell, so the live
/// count stays non-zero.
public final class BoxableTrackedCell: Boxable {
    private let ref: TrackedRef

    public init() { self.ref = TrackedRef(tag: 0) }
    public init(value: Int32) { self.ref = TrackedRef(tag: value) }

    public func boxedValue() -> Int32 { ref.tag }
}

/// Async scalar OPAQUE existential return. The completion callback faults the awaiting
/// Task (proxy suppressed); it must first value-witness-Destroy the `ExistentialContainer1`
/// carrier so the embedded `TrackedRef`'s +1 is released. A leak pins one ref per call.
public func fetchTrackedBoxableScalar(_ seed: Int32) async -> any Boxable {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return BoxableTrackedCell(value: seed)
}

/// Async COLLECTION existential return. The async wrapper copies `[any Boxable]` into the
/// carrier (a +1 on the array's copy-on-write storage backing every element). The completion
/// callback must value-witness-Destroy the `SwiftArray<ExistentialContainer1>` carrier before
/// faulting, or the storage — and every embedded `TrackedRef` — leaks once per call.
public func fetchTrackedBoxableArray(_ count: Int32) async -> [any Boxable] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: [any Boxable] = []
    for i in 0..<count {
        result.append(BoxableTrackedCell(value: i))
    }
    return result
}

/// Async DICTIONARY existential return — the value type `any Boxable` is the suppressed proxy. The
/// async wrapper copies `[Int32: any Boxable]` into the carrier (a +1 on the dictionary's
/// copy-on-write storage backing every value). The completion callback must value-witness-Destroy
/// the `SwiftDictionary<…>` carrier before faulting, or the storage — and every embedded
/// `TrackedRef` — leaks once per call. Shares the array's collection arm; included so that arm has
/// runtime proof on a dictionary shape, not just an array.
public func fetchTrackedBoxableDictionary(_ count: Int32) async -> [Int32: any Boxable] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    var result: [Int32: any Boxable] = [:]
    for i in 0..<count {
        result[i] = BoxableTrackedCell(value: i)
    }
    return result
}

/// Async OPTIONAL-of-COLLECTION existential return — the already-shipped suppressed arm,
/// kept as a regression guard. Same carrier +1 on the inner array's CoW storage; the
/// completion callback must Destroy the carrier before faulting.
public func fetchTrackedBoxableArrayOptional(_ count: Int32) async -> [any Boxable]? {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if count < 0 { return nil }
    var result: [any Boxable] = []
    for i in 0..<count {
        result.append(BoxableTrackedCell(value: i))
    }
    return result
}

// MARK: Tracked conformer for the CLASS-BOUND `any …: AnyObject` arm

/// Class-bound (`AnyObject`) suppressed-proxy protocol: the `init()` requirement makes
/// EveryProtocol unable to conform, so its universal proxy is suppressed exactly like
/// `Boxable` — but because it is `AnyObject`-constrained, `any TrackedClassBoxable` is a
/// 16-byte class existential (`ClassExistentialContainer1`), whose carrier word 0 is a
/// bare class reference. The fault-path release for this shape is a direct
/// `swift_unknownObjectRelease` on that word, NOT an opaque value-witness Destroy.
public protocol TrackedClassBoxable: AnyObject {
    init()
    func trackedTag() -> Int32
}

/// `final class` conformer of the class-bound suppressed protocol, embedding a
/// `LifetimeTracker`-counted `TrackedRef`. A leaked class-existential carrier pins this
/// instance (and its embedded ref) per call.
public final class TrackedClassBoxableCell: TrackedClassBoxable {
    private let ref: TrackedRef

    public init() { self.ref = TrackedRef(tag: 0) }
    public init(value: Int32) { self.ref = TrackedRef(tag: value) }

    public func trackedTag() -> Int32 { ref.tag }
}

/// Async scalar CLASS-BOUND existential return. The completion callback faults the awaiting
/// Task (proxy suppressed); it must first release the `ClassExistentialContainer1` carrier's
/// class reference via `swift_unknownObjectRelease`, deallocating the cell and its embedded
/// `TrackedRef`. A leak pins one ref per call.
public func fetchTrackedClassBoxableScalar(_ seed: Int32) async -> any TrackedClassBoxable {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return TrackedClassBoxableCell(value: seed)
}
