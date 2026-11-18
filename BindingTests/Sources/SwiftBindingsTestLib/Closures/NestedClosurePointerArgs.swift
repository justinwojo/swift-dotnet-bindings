// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Pointers in a nested closure's outer arguments
//
// A closure that takes a pointer and an inner closure goes through the nested-closure bridge. The
// bridge passed every non-primitive outer argument as an object reference
// (Unmanaged.passUnretained), which does not compile for a pointer, so each of these members was
// withdrawn. A raw or opaque pointer crosses the C slot as itself and a typed pointer as a raw
// pointer. Each member hands the callback a pointer to a value and returns what the callback
// reported back through the inner closure.

public final class NestedPointerCallbackHost {
    public init() {}

    /// Reads through `UnsafePointer<Int32>` and reports the value doubled.
    public func readThroughTypedPointer(_ value: Int32, _ body: (UnsafePointer<Int32>, (Int32) -> Void) -> Void) -> Int32 {
        var cell = value
        var reported: Int32 = 0
        withUnsafePointer(to: &cell) { p in body(p) { reported = $0 } }
        return reported
    }

    /// The callback writes through `UnsafeMutablePointer<Int32>`; Swift returns what it wrote
    /// plus what it reported.
    public func writeThroughMutablePointer(_ body: (UnsafeMutablePointer<Int32>, (Int32) -> Void) -> Void) -> Int32 {
        var cell: Int32 = 0
        var reported: Int32 = 0
        withUnsafeMutablePointer(to: &cell) { p in body(p) { reported = $0 } }
        return cell * 1000 + reported
    }

    /// A raw mutable pointer to an Int64; the callback adds one through it.
    public func incrementThroughRawPointer(_ value: Int64, _ body: (UnsafeMutableRawPointer, (Int32) -> Void) -> Void) -> Int64 {
        var cell = value
        var reported: Int32 = 0
        withUnsafeMutablePointer(to: &cell) { p in body(UnsafeMutableRawPointer(p)) { reported = $0 } }
        return cell + Int64(reported)
    }

    /// A read-only raw pointer to a Double.
    public func readThroughRawPointer(_ value: Double, _ body: (UnsafeRawPointer, (Double) -> Void) -> Void) -> Double {
        var cell = value
        var reported = 0.0
        withUnsafePointer(to: &cell) { p in body(UnsafeRawPointer(p)) { reported = $0 } }
        return reported
    }

    /// A `@Sendable` inner closure beside a typed pointer. The bridge boxes the inner closure and
    /// casts it back to its Swift function type to call it; a boxed `@Sendable` closure does not
    /// cast to the plain function type, so the cast must say `@Sendable` or the first report traps.
    public func readThroughTypedPointerReportingSendably(_ value: Int32, _ body: (UnsafePointer<Int32>, @Sendable (Int32) -> Void) -> Void) -> Int32 {
        let report = NestedSendableReport()
        var cell = value
        withUnsafePointer(to: &cell) { p in body(p) { report.value = $0 } }
        return report.value
    }

    /// The same `@Sendable` inner closure beside a scalar, a shape the bridge emitted before
    /// pointers did.
    public func reportSendably(_ value: Int32, _ body: (Int32, @Sendable (Int32) -> Void) -> Void) -> Int32 {
        let report = NestedSendableReport()
        body(value) { report.value = $0 }
        return report.value
    }

    /// The inner closure takes the pointer: the callback passes the address of its own Int32 and
    /// Swift reads through it.
    public func readThroughInnerTypedPointer(_ seed: Int32, _ body: (Int32, (UnsafePointer<Int32>) -> Void) -> Void) -> Int32 {
        var read: Int32 = 0
        body(seed) { p in read = p.pointee }
        return read
    }

    /// Swift writes through the callback's `UnsafeMutablePointer<Int64>`.
    public func writeThroughInnerMutablePointer(_ body: ((UnsafeMutablePointer<Int64>) -> Void) -> Void) {
        body { p in p.pointee = (1 << 33) + 5 }
    }

    /// Swift reads the callback's Double through `UnsafeRawPointer`.
    public func readThroughInnerRawPointer(_ body: ((UnsafeRawPointer) -> Void) -> Void) -> Double {
        var read = 0.0
        body { p in read = p.load(as: Double.self) }
        return read
    }
}

private final class NestedSendableReport: @unchecked Sendable {
    var value: Int32 = 0
}
