// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ArraySlice parameters beside inout parameters
//
// The ArraySlice wrapper re-slices its Array argument before forwarding; the `inout` siblings in
// the same signature still have to reach the callee as writable storage.

public final class SliceTextAccumulator {
    public var text: String = ""

    public init() {}
}

public enum SliceWhitespace {
    /// Appends `bytes`, collapsing runs of spaces across calls through `lastWasWhite`.
    public static func normalise(
        _ accumulator: SliceTextAccumulator, bytes: ArraySlice<UInt8>, stripLeading: Bool, lastWasWhite: inout Bool
    ) {
        for byte in bytes {
            let white = byte == 32
            if !(white && (lastWasWhite || (stripLeading && accumulator.text.isEmpty))) {
                accumulator.text.append(Character(UnicodeScalar(byte)))
            }
            lastWasWhite = white
        }
    }

    /// As above, also reporting whether `bytes` held any space.
    public static func normalise(
        _ accumulator: SliceTextAccumulator, bytes: ArraySlice<UInt8>, stripLeading: Bool,
        lastWasWhite: inout Bool, sawWhite: inout Bool
    ) {
        normalise(accumulator, bytes: bytes, stripLeading: stripLeading, lastWasWhite: &lastWasWhite)
        sawWhite = bytes.contains(32)
    }
}
