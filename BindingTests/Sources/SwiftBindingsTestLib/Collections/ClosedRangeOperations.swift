// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ClosedRange as Parameter
// These exercise the C# → Swift direction: a SwiftClosedRange<Bound> built in
// managed code is marshalled across @_cdecl and consumed by Swift via the
// generated UnsafeRawPointer reconstruction path.

/// Returns the lower bound of an Int closed range.
public func closedRangeLowerInt(_ range: ClosedRange<Int>) -> Int {
    return range.lowerBound
}

/// Returns the upper bound of an Int closed range.
public func closedRangeUpperInt(_ range: ClosedRange<Int>) -> Int {
    return range.upperBound
}

/// Returns the count of integers in a closed range (inclusive of both endpoints).
public func closedRangeCountInt(_ range: ClosedRange<Int>) -> Int {
    return range.count
}

/// Returns true if the value lies inside the closed range.
public func closedRangeContainsInt(_ range: ClosedRange<Int>, value: Int) -> Bool {
    return range.contains(value)
}

/// Sums the integers inside a closed range — useful for verifying both bounds
/// were marshalled into the same struct (a swap would still pass lower/upper
/// individual reads but would skew the sum).
public func closedRangeSumInt(_ range: ClosedRange<Int>) -> Int {
    return range.reduce(0, +)
}

// MARK: - ClosedRange as Return
// These exercise the Swift → C# direction: a Swift-built ClosedRange<Bound> is
// returned through @_cdecl and reconstructed into SwiftClosedRange<Bound>
// managed-side via NewFromPayload / VWT InitializeWithCopy.

/// Builds a ClosedRange<Int> from two endpoints (lower must be <= upper).
public func makeClosedRangeInt(lower: Int, upper: Int) -> ClosedRange<Int> {
    return lower...upper
}

/// Builds a ClosedRange<Int64> — separate Bound type to exercise per-instantiation
/// metadata caching and a different value witness table.
public func makeClosedRangeInt64(lower: Int64, upper: Int64) -> ClosedRange<Int64> {
    return lower...upper
}

/// Builds a ClosedRange<Double> — exercises the FPR-class Bound path through the
/// canonical prespecialized metadata accessor.
public func makeClosedRangeDouble(lower: Double, upper: Double) -> ClosedRange<Double> {
    return lower...upper
}

/// Builds a ClosedRange<String> — exercises a non-trivial Bound (refcounted payload,
/// VWT-managed copy/destroy). The "@frozen + ref-typed bounds" combination is the
/// trickiest case for the stride-based offset path.
public func makeClosedRangeString(lower: String, upper: String) -> ClosedRange<String> {
    return lower...upper
}

// MARK: - Round-trip
// Take a ClosedRange in and emit a new one out, optionally widened. Exercises both
// directions and the alloc/copy/free cycle.

/// Returns a new ClosedRange shifted by the supplied delta on both endpoints.
public func shiftedClosedRangeInt(_ range: ClosedRange<Int>, by delta: Int) -> ClosedRange<Int> {
    return (range.lowerBound + delta)...(range.upperBound + delta)
}

// MARK: - Optional<ClosedRange> as Parameter
// Exercises the C# → Swift direction for Optional<ClosedRange<Bound>>. ClosedRange<Float>
// is an @frozen 8-byte value, so Optional<ClosedRange<Float>> has no extra inhabitants:
// the managed SwiftOptional<SwiftClosedRange<Float>> packs the range value plus an appended
// tag byte (size 9, tag at offset 8) into one buffer and passes a pointer to it. Float is
// the exact bound the PhysicsRevoluteJoint(angularLimit:) constructor uses.

/// Returns the lower bound of an optional Float range, or -1 when nil — proves the Some
/// payload (the range value) survives the tagged-optional pack/unpack across @_cdecl.
public func optionalClosedRangeLowerFloat(_ range: ClosedRange<Float>?) -> Float {
    return range?.lowerBound ?? -1
}

/// Returns the span (upper - lower) of an optional Float range, or -1 when nil — reads
/// BOTH endpoints so a single-bound copy or a swapped pack would be caught.
public func optionalClosedRangeSpanFloat(_ range: ClosedRange<Float>?) -> Float {
    guard let range else { return -1 }
    return range.upperBound - range.lowerBound
}

/// True iff the optional range is nil — proves None marshals as the None tag byte, not a
/// spurious Some pointing at zeroed payload bytes (which would read as a valid 0...0 range).
public func optionalClosedRangeIsNilFloat(_ range: ClosedRange<Float>?) -> Bool {
    return range == nil
}

// MARK: - Optional<ClosedRange> as Return
// Swift → C# direction: an Optional<ClosedRange<Float>> returned through @_cdecl and
// reconstructed into SwiftOptional<SwiftClosedRange<Float>> managed-side.

/// Returns Some(lower...upper) when shouldReturn is true, otherwise nil.
public func makeOptionalClosedRangeFloat(lower: Float, upper: Float, shouldReturn: Bool) -> ClosedRange<Float>? {
    return shouldReturn ? lower...upper : nil
}

// MARK: - [Optional<ClosedRange>] as Parameter
// Exercises the container-element path: an array of optional Float ranges marshals as
// SwiftArray<SwiftOptional<SwiftClosedRange<Float>>> — both the array element generic and the
// per-element SwiftOptional generic must name the handle-backed wrapper, not a `.Buffer` struct.

/// Sums the span (upper - lower) of every non-nil range in the array; nil entries contribute 0.
/// Reads both endpoints of each Some element out of the packed array buffer.
public func sumOptionalClosedRangeSpansFloat(_ ranges: [ClosedRange<Float>?]) -> Float {
    return ranges.reduce(0) { acc, r in acc + (r.map { $0.upperBound - $0.lowerBound } ?? 0) }
}

/// Counts the nil entries — proves None elements pack as the None tag, distinct from a Some over
/// zeroed payload bytes.
public func countNilClosedRangesFloat(_ ranges: [ClosedRange<Float>?]) -> Int {
    return ranges.filter { $0 == nil }.count
}
