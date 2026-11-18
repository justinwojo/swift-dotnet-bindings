// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Typed pointers in closure positions
//
// A typed pointer crosses a callback's @convention(c) slot as a raw pointer. An
// `UnsafeMutablePointer<T>` converts to that slot implicitly, but an `UnsafePointer<T>` does not,
// and neither converts back from a raw pointer when managed code returns one, so the wrapper
// adapter has to convert in both directions. These members cover the argument and result
// positions, both directions of the value flow, and the closure-bearing member kinds the
// adapter serves (method, escaping and stored handler, throwing, free function, initializer,
// closure-typed property). Every value is checked through the pointer, not only compiled.

/// The render-callback shape: a mutable flag, a read-only count, a frame number and a raw
/// output buffer, returning a status.
public typealias TypedPointerRenderHandler =
    (UnsafeMutablePointer<ObjCBool>, UnsafePointer<Int>, UInt32, UnsafeMutableRawPointer) -> Int32

@frozen
public struct TypedPointerSample {
    public var id: Int32
    public var weight: Double
    public init(id: Int32, weight: Double) { self.id = id; self.weight = weight }
}

@MainActor
open class TypedPointerRenderNode {
    private var handler: TypedPointerRenderHandler?
    public init() {}

    /// Stores the escaping handler; `render(count:frame:)` calls it later.
    public func install(_ handler: @escaping TypedPointerRenderHandler) {
        self.handler = handler
    }

    /// Calls the stored handler with a flag seeded false, `count` behind a read-only pointer and
    /// a four-Int32 output buffer. Returns status * 1_000_000 + flag * 100_000 + the buffer's sum.
    public func render(count: Int, frame: UInt32) -> Int {
        guard let handler else { return -1 }
        var stop = ObjCBool(false)
        var countValue = count
        var buffer: [Int32] = [0, 0, 0, 0]
        let status = buffer.withUnsafeMutableBytes { raw in
            withUnsafeMutablePointer(to: &stop) { stopPtr in
                withUnsafePointer(to: &countValue) { countPtr in
                    handler(stopPtr, countPtr, frame, raw.baseAddress!)
                }
            }
        }
        return Int(status) * 1_000_000 + (stop.boolValue ? 100_000 : 0) + Int(buffer.reduce(0, +))
    }

    /// The same handler shape, non-escaping and with a throwing method around it.
    public func renderOnce(count: Int, frame: UInt32, _ handler: TypedPointerRenderHandler) throws -> Int32 {
        var stop = ObjCBool(false)
        var countValue = count
        var buffer: [Int32] = [0, 0]
        return buffer.withUnsafeMutableBytes { raw in
            withUnsafeMutablePointer(to: &stop) { stopPtr in
                withUnsafePointer(to: &countValue) { countPtr in
                    handler(stopPtr, countPtr, frame, raw.baseAddress!)
                }
            }
        } + (stop.boolValue ? 1000 : 0) + buffer[0] + buffer[1]
    }
}

public final class TypedPointerClosureHost {
    public var reader: ((UnsafePointer<Int32>) -> Int32)?
    public let seededTotal: Int32

    /// An initializer taking the read-only pointer callback: it is called once with `seed`.
    public init(seed: Int32, _ reader: @escaping (UnsafePointer<Int32>) -> Int32) {
        var value = seed
        seededTotal = withUnsafePointer(to: &value) { reader($0) }
        self.reader = reader
    }

    /// Reads `value` through the stored property's callback; -1 when none is set.
    public func readThroughProperty(_ value: Int32) -> Int32 {
        guard let reader else { return -1 }
        var v = value
        return withUnsafePointer(to: &v) { reader($0) }
    }

    /// A struct pointee behind a read-only pointer.
    public func readSample(id: Int32, weight: Double, _ reader: (UnsafePointer<TypedPointerSample>) -> Double) -> Double {
        var sample = TypedPointerSample(id: id, weight: weight)
        return withUnsafePointer(to: &sample) { reader($0) }
    }

    /// The callback returns a read-only pointer; Swift reads `count` Int32s through it.
    public func sumReturned(count: Int, _ provider: () -> UnsafePointer<Int32>) -> Int32 {
        let p = provider()
        var total: Int32 = 0
        for i in 0..<count { total += p[i] }
        return total
    }

    /// The callback returns a mutable pointer; Swift writes `value` through it and reads it back.
    public func writeReturned(_ value: Int32, _ provider: () -> UnsafeMutablePointer<Int32>) -> Int32 {
        let p = provider()
        p.pointee = value
        return p.pointee
    }

    /// Throwing callback returning a read-only pointer to a struct.
    public func weightReturned(_ provider: () throws -> UnsafePointer<TypedPointerSample>) throws -> Double {
        let p = try provider()
        return p.pointee.weight * 2 + Double(p.pointee.id)
    }
}

/// A free function with a read-only pointer argument and a read-only pointer result.
public func typedPointerEcho(_ value: Int32, _ transform: (UnsafePointer<Int32>) -> UnsafePointer<Int32>) -> Int32 {
    var v = value
    return withUnsafePointer(to: &v) { transform($0).pointee }
}
