// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foundation.Data param straddling the integer-register boundary

/// Pins the `@_cdecl` decompose path for a `Foundation.Data` parameter that lands at or beyond the
/// eighth integer register.
///
/// `Foundation.Data` lowers to a two-word inline value. When seven `Int64` arguments precede it, the
/// first six fill `x0..x5`, the seventh fills `x6`, and `Data`'s two words straddle `x7` + the stack.
/// The generated `@_cdecl` wrapper must decompose `Data` into two explicit pointer-width words
/// (`payload_w0` / `payload_w1`) so the C# P/Invoke matches the Swift wrapper's register layout. Before
/// the fix the wrapper passed `Data` as a single by-value remapped struct, so the second word and every
/// later argument shifted by one slot — the byte payload and trailing arguments came through as garbage.
/// Exercised by `DataRegisterStraddleTests`. Only `Data`-as-parameter is used here (no `Data` return,
/// no `Optional<Data>`) to keep the fixture on the supported marshalling path.
public class BlobPacker {
    public let leadingSum: Int64
    public let payloadSum: Int64

    /// Throwing-free init with seven leading `Int64` args followed by a `Data` payload, forcing the
    /// payload past the integer-register boundary in the `@_cdecl` constructor wrapper.
    public init(a0: Int64, a1: Int64, a2: Int64, a3: Int64, a4: Int64, a5: Int64, a6: Int64, payload: Data) {
        self.leadingSum = a0 &+ a1 &+ a2 &+ a3 &+ a4 &+ a5 &+ a6
        self.payloadSum = payload.reduce(Int64(0)) { $0 &+ Int64($1) }
    }

    /// Instance method with seven leading `Int64` args followed by a `Data` payload, forcing the payload
    /// past the integer-register boundary in the `@_cdecl` method wrapper. Returns the sum of the leading
    /// arguments plus every byte of `extra`, so a C# test can confirm both survived the register layout.
    public func repack(b0: Int64, b1: Int64, b2: Int64, b3: Int64, b4: Int64, b5: Int64, b6: Int64, extra: Data) -> Int64 {
        let argSum = b0 &+ b1 &+ b2 &+ b3 &+ b4 &+ b5 &+ b6
        let byteSum = extra.reduce(Int64(0)) { $0 &+ Int64($1) }
        return argSum &+ byteSum
    }
}
