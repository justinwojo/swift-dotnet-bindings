// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Large-Optional ABI on a wrapper-ineligible parent
//
// Models the shape that reaches the *direct CallConvSwift* fallback while
// carrying an Optional whose Swift representation is WIDER than one machine
// word. `InternalTypeReach.swift` already covers the sync-fallback decision
// itself (arm 2b, `parent_module_internal`); this fixture is specifically
// about what the fallback P/Invoke's ABI must look like once that decision has
// been made.
//
// The parent is `@usableFromInline internal`, so no Swift wrapper can be
// emitted for these members (a wrapper body would have to name the internal
// parent, which the separate wrapper-compilation module cannot reference).
// Every member below therefore lands on a direct CallConvSwift P/Invoke
// against the member's own exported ABI symbol. Because the members are
// `public static` on a caseless enum, the emitted C# shell is callable with no
// instance — unlike `InternalHolder`, whose internal `init` makes it
// absence-assertion-only. That is what makes these runtime-testable.
//
// Swift's physical lowering for the return/parameter shapes here (verified
// against the compiled arm64 IR):
//
//   * `String?`   — 16 bytes, extra-inhabitant (String has spare bits, so the
//                   Optional needs no tag byte). Returned in x0+x1; passed as
//                   two integer words. NOT an indirect/sret result.
//   * `Double?`   — 9 bytes: an 8-byte payload plus a separate tag byte,
//                   because Double has no spare bits to steal.
//   * `[String]?` — 8 bytes: Array is a single refcounted pointer and the
//                   Optional uses the null extra inhabitant. One machine word.
//
// Only the third fits in the single `IntPtr` slot a naive direct P/Invoke
// gives it. The first two do not, which is the ABI hazard this fixture pins.
//
// The `[String]?` member is here on purpose as a NEGATIVE control: it is
// classified "large" by the generator's inner-type heuristic even though its
// real width is one word, so it proves the ABI gate keys on the actual
// physical lowering rather than on that heuristic. If it ever starts failing
// closed, the gate has become over-broad and is stripping members that work.

/// `@usableFromInline internal` caseless enum with `public static` members —
/// the namespace-with-statics shape. The parent's internal visibility is the
/// only reason a Swift wrapper is unavailable; each member's own signature is
/// public-only and otherwise perfectly ordinary.
@usableFromInline
internal enum InternalOptionalAbiHost {

    // MARK: Return side — two-word Optional (extra-inhabitant)

    /// Returns `nil` for a negative seed and a real String otherwise. This is
    /// the canonical two-word `Optional<String>` return on the direct
    /// CallConvSwift path: 16 bytes arriving in x0+x1, where a single-`IntPtr`
    /// P/Invoke return would capture only the first word and leave the
    /// discriminating second word as whatever the caller's stack slot happened
    /// to contain.
    ///
    /// The nil case is the load-bearing one. `Optional<String>` carries no tag
    /// byte, so nil-ness is decided by the value witness reading ALL 16 bytes —
    /// truncation means uninitialized stack decides Some vs None.
    public static func label(forSeed seed: Int32) -> String? {
        if seed < 0 { return nil }
        return "label-\(seed)"
    }

    /// Empty-string Some. Distinct from nil at the ABI level but easy to
    /// confuse with it when the second word is garbage — the reported live
    /// symptom was exactly a nil value decoding as `""` on one runtime and
    /// `null` on the other.
    public static func emptyLabel() -> String? {
        return ""
    }

    /// Long, heap-allocated (non-small-form) String Some. A small String is
    /// stored inline across both words; a long one puts a pointer to a heap
    /// buffer in one word. Both must survive, so both forms are covered.
    public static func longLabel() -> String? {
        return String(repeating: "swift-optional-abi-", count: 8)
    }

    // MARK: Return side — payload-plus-tag-byte Optional

    /// `Double?` is 9 bytes: Double has no spare bits, so the Optional appends
    /// a discriminator byte after the 8-byte payload. A single-word return
    /// captures the payload and drops the tag entirely.
    public static func timestamp(forSeed seed: Int32) -> Double? {
        if seed < 0 { return nil }
        return Double(seed) + 0.5
    }

    // MARK: Return side — genuine single-word Optional (NEGATIVE control)

    /// `[String]?` is one machine word: Array is a single refcounted pointer
    /// and nil is its null extra inhabitant. The existing single-slot direct
    /// P/Invoke is already ABI-correct here, so this member must keep binding
    /// and keep working. It exists to catch an over-broad fail-closed gate.
    public static func names(forSeed seed: Int32) -> [String]? {
        if seed < 0 { return nil }
        return ["a-\(seed)", "b-\(seed)"]
    }

    // MARK: Parameter side

    /// Two-word `Optional<String>` in argument position on the same
    /// wrapper-ineligible path. Swift takes it as two integer argument words;
    /// supplying only the first leaves the second word undefined, so the
    /// callee's own nil check reads garbage. Returns a value that distinguishes
    /// nil from every Some, so a truncated argument cannot accidentally agree.
    public static func labelWidth(_ label: String?) -> Int32 {
        guard let label else { return -1 }
        return Int32(label.utf8.count)
    }

    /// Round-trips a two-word Optional through both directions in one call, so
    /// a parameter-side and return-side fix cannot pass independently while the
    /// pair is inconsistent.
    public static func echoLabel(_ label: String?) -> String? {
        guard let label else { return nil }
        return label + "!"
    }

    /// Payload-plus-tag-byte Optional in argument position.
    public static func timestampWhole(_ value: Double?) -> Int32 {
        guard let value else { return -1 }
        return Int32(value)
    }

    /// Single-word Optional in argument position.
    ///
    /// NOTE: unlike `names(forSeed:)`, this member does NOT stay live — but not
    /// because of the width floor, which correctly leaves it alone. A separate,
    /// pre-existing floor tombstones every member on an internal parent whose
    /// P/Invoke signature is non-blittable, and that blittability test counts
    /// any Optional in *argument* position as a generic container regardless of
    /// its real width (the return side has no such arm, which is why `names`
    /// survives and this does not). The genuine parameter-side width control
    /// therefore lives on `GenericOptionalAbiBox`, whose public parent keeps
    /// that other floor out of the picture.
    public static func nameCount(_ names: [String]?) -> Int32 {
        guard let names else { return -1 }
        return Int32(names.count)
    }
}

/// Public, constructible generic struct carrying the same large-Optional
/// shapes. A generic parent is one of the wrapper-ineligibility conditions, so
/// these members decline the Optional-pointer wrapper and land on the direct
/// CallConvSwift fallback — the same fallback the internal-parent members above
/// would land on if their wrapper were declined rather than dangling.
///
/// Unlike the internal-parent host, this type is public and constructible from
/// C#, so its members are callable at runtime and can assert the actual
/// marshalled values rather than mere presence/absence.
public struct GenericOptionalAbiBox<Tag> {
    public let seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    /// Two-word `Optional<String>` return on the direct fallback path.
    public func label() -> String? {
        if seed < 0 { return nil }
        return "boxed-\(seed)"
    }

    /// Payload-plus-tag-byte `Optional<Double>` return on the same path.
    public func timestamp() -> Double? {
        if seed < 0 { return nil }
        return Double(seed) + 0.25
    }

    /// Genuine single-word `Optional<[String]>` return — negative control.
    public func names() -> [String]? {
        if seed < 0 { return nil }
        return ["g-\(seed)"]
    }

    /// Two-word `Optional<String>` in argument position.
    public func width(of label: String?) -> Int32 {
        guard let label else { return -1 }
        return Int32(label.utf8.count)
    }

    /// Genuine single-word `Optional<[String]>` in ARGUMENT position — the
    /// parameter-side over-breadth control, and the counterpart to `names()`.
    /// The return side and the parameter side are separate arms of the width
    /// floor, so a regression that tombstones only one of them needs a control
    /// on both. (The equivalent member on the internal-parent host cannot serve
    /// as this control: a different, pre-existing floor tombstones it there for
    /// non-blittability, which is why this one lives on the public parent.)
    public func nameCount(of names: [String]?) -> Int32 {
        guard let names else { return -1 }
        return Int32(names.count)
    }

    /// `Optional` of the parent's own generic parameter, in argument position.
    /// Swift passes a generic Optional INDIRECTLY — the caller has no static
    /// size for it — so the direct path already hands Swift the buffer address
    /// rather than a value word. The pointer is a carrier for the whole value,
    /// so there is nothing to truncate and this must keep binding.
    public func tagIsPresent(_ tag: Tag?) -> Bool {
        return tag != nil
    }

    /// A pointer Optional. `OpaquePointer?` measures 8 bytes — one machine word,
    /// nil riding the null extra inhabitant — so on width alone it looks like it
    /// belongs with `[String]?` among the members that keep binding.
    ///
    /// It does not, and this member exists to keep that fact pinned. Calling it
    /// with the floor lifted SIGSEGVs the simulator on the first call, the same
    /// as the 16-byte `String?` argument and unlike the genuinely-fine 8-byte
    /// `[String]?` argument. Being one word wide is necessary for the direct
    /// argument slot to work but not sufficient, so this shape must stay
    /// refused: "provably 8 bytes" is not a licence to emit the call. A future
    /// change that widens the classifier to nullable pointers on the strength of
    /// their measured size alone would ship a crash, which is why the negative
    /// result is recorded as a test rather than as a comment.
    public func opaqueWidth(of handle: OpaquePointer?) -> Int32 {
        return handle == nil ? -1 : 8
    }

    // MARK: Accessor shapes
    //
    // A `public var x: String?` truncates exactly like the equivalent method —
    // the getter is the same direct CallConvSwift call with the same 16-byte
    // return. It is also the single most ordinary shape a Swift API has, so the
    // ABI floor covers accessors on purpose. These two properties exist to
    // exercise the property EMISSION path rather than just the floor's
    // decision: a member-level refusal has to surface on the public property a
    // consumer actually writes, not only on the private synthesized accessor
    // the property delegates to.

    /// Two-word `Optional<String>` through a property getter. Must fail closed.
    public var boxedLabel: String? {
        if seed < 0 { return nil }
        return "prop-\(seed)"
    }

    /// Genuine single-word `Optional<[String]>` through a property getter —
    /// the accessor-side negative control. Must keep binding and keep working.
    public var boxedNames: [String]? {
        if seed < 0 { return nil }
        return ["p-\(seed)"]
    }

    /// An ObjC-bridgeable Swift *value* type. `Foundation.URL` is a struct, not
    /// a class, so on the direct CallConvSwift path it keeps its native Swift
    /// layout (16 bytes) rather than arriving as an NSURL pointer — bridging
    /// only happens at an ObjC/`@_cdecl` boundary, and this path has neither.
    ///
    /// This is the subtlest member of the fixture and the reason the width
    /// floor cannot delegate to the routing predicates. Those predicates class
    /// this payload as a *reference* (it does bridge to an object, just not
    /// here), so before the floor keyed on physical width the emitter produced
    /// a single-`IntPtr` P/Invoke and then reinterpreted that first word as an
    /// ObjC object pointer with `GetINativeObject(..., owns: true)` — reading
    /// half a struct as an object AND releasing it. Strictly worse than the
    /// truncation this fix is named for, and reachable on an ordinary public
    /// generic parent. Must fail closed.
    public func boxedUrl() -> Foundation.URL? {
        if seed < 0 { return nil }
        return URL(string: "https://example.invalid/\(seed)")
    }
}
