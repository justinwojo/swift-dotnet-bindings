// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Nested-field inline size inside a frozen struct's Buffer mirror
//
// A `@frozen` struct carrying reference fields is projected as a C# class with a nested `Buffer`
// struct that mirrors the Swift value's inline layout byte for byte. The Buffer is what gets
// `NativeMemory.Alloc`'d and handed to Swift, so every stored field's inline SIZE and ALIGNMENT has
// to match Swift's real layout: an under-sized field shortens the whole mirror and Swift then writes
// past the end of the allocation on every copy in or out.
//
// The shape that matters here is a stored field whose own type is ANOTHER reference-bearing frozen
// struct. Such a field is not one pointer wide — it is as wide as the nested struct's own inline
// layout (a single `String` field alone is two words). The three field shapes below are the ones a
// field-size oracle has to get right, and they are deliberately mixed in ONE host so a wrong width on
// any of them shifts every later field:
//
//   * `leading`  — a nested reference-bearing frozen struct (2 words in Swift).
//   * `optional` — the same nested struct under `Optional`. Its payload has spare bits, so Swift
//                  appends no discriminator and the field stays exactly as wide as the payload.
//   * `trivial`  — a nested frozen struct with only trivial fields (8 bytes, 4-byte aligned), which
//                  additionally fences the alignment math: the field after it must sit at the next
//                  4-byte boundary, not at a word boundary.
//
// `sentinel` is the neighbour-corruption probe. It is the LAST field, so it lives at the highest
// offset and is the first thing a short mirror puts out of bounds; a test that reads it back after a
// Swift round-trip fails loudly when any earlier field was mis-sized.
//
// The `label` strings the tests use are longer than 15 UTF-8 bytes on purpose, so Swift's small-string
// optimisation does NOT apply and the `String` really does carry a heap reference — the case where a
// short mirror corrupts a refcounted object rather than only scratch bytes.
//
// CONSTRUCTION NOTE: every type here is built through a static `make`/`roundTrip` factory rather than
// a public initializer. A Swift `init` takes its parameters `@owned`, and the direct-P/Invoke path
// currently hands them over borrowed; routing construction through a `func` keeps these tests fencing
// the Buffer layout instead of tripping over parameter ownership on the way in.

// MARK: Leaves

/// Reference-bearing `@frozen` leaf: one `String`, so two words inline and refcounted.
@frozen
public struct NestedLayoutRefLeaf {
    public let label: String

    internal init(label: String) {
        self.label = label
    }

    /// Factory (not an initializer) — see the construction note above.
    public static func make(label: String) -> NestedLayoutRefLeaf {
        return NestedLayoutRefLeaf(label: label)
    }

    public var text: String {
        return label
    }
}

/// Trivial `@frozen` leaf: two `Int32`s, so 8 bytes at 4-byte alignment. Sized and aligned unlike a
/// word, which is what makes it useful as the alignment fence in the host below.
@frozen
public struct NestedLayoutTrivialLeaf {
    public let first: Int32
    public let second: Int32

    internal init(first: Int32, second: Int32) {
        self.first = first
        self.second = second
    }

    public static func make(first: Int32, second: Int32) -> NestedLayoutTrivialLeaf {
        return NestedLayoutTrivialLeaf(first: first, second: second)
    }

    public var sum: Int32 {
        return first &+ second
    }
}

// MARK: Host

/// `@frozen` host mixing the three nested-field shapes. Reference fields make it a Buffer-projected
/// class, so its C# mirror has to reproduce this exact layout.
@frozen
public struct NestedFieldLayoutHost {
    public let leading: NestedLayoutRefLeaf
    public let optional: NestedLayoutRefLeaf?
    public let trivial: NestedLayoutTrivialLeaf
    public let sentinel: Int32

    internal init(
        leading: NestedLayoutRefLeaf,
        optional: NestedLayoutRefLeaf?,
        trivial: NestedLayoutTrivialLeaf,
        sentinel: Int32
    ) {
        self.leading = leading
        self.optional = optional
        self.trivial = trivial
        self.sentinel = sentinel
    }

    /// Factory (not an initializer) — see the construction note above. `includeOptional` selects
    /// between the populated and the `nil` optional field without needing an `Optional` parameter.
    public static func make(
        label: String,
        includeOptional: Bool,
        first: Int32,
        second: Int32,
        sentinel: Int32
    ) -> NestedFieldLayoutHost {
        return NestedFieldLayoutHost(
            leading: NestedLayoutRefLeaf(label: label),
            optional: includeOptional ? NestedLayoutRefLeaf(label: label + "-optional") : nil,
            trivial: NestedLayoutTrivialLeaf(first: first, second: second),
            sentinel: sentinel
        )
    }

    /// Passes the whole host into Swift and back out again — one full Buffer copy in each direction.
    public static func roundTrip(_ value: NestedFieldLayoutHost) -> NestedFieldLayoutHost {
        return value
    }

    public var leadingText: String {
        return leading.label
    }

    public var leadingLeaf: NestedLayoutRefLeaf {
        return leading
    }

    public var hasOptional: Bool {
        return optional != nil
    }

    /// Reads the optional field's payload without returning an `Optional`, so the assertion stays on
    /// the nested field's bytes rather than on optional projection.
    public var optionalText: String {
        return optional?.label ?? "<none>"
    }

    public var trivialFirst: Int32 {
        return trivial.first
    }

    public var trivialSecond: Int32 {
        return trivial.second
    }

    /// The neighbour-corruption probe: the highest-offset field in the struct.
    public var sentinelValue: Int32 {
        return sentinel
    }
}
