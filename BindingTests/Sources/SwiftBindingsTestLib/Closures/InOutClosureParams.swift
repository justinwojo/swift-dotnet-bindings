// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// `inout` parameters in a closure's OWN signature — `(inout [String: Any]) -> Void`.
//
// Shape observed in a mapping SDK, where an entire option-builder family is written this
// way: each builder seeds a dictionary, hands it to the caller's block by reference, and
// returns whatever the block left behind. Because the mutation travels back through the
// `inout` cell rather than through the closure's return value, a bridge that marshals the
// argument by value delivers a member that compiles and silently discards every edit the
// consumer makes — so the shape used to refuse wholesale, taking the whole family with it.
//
// The members below pin the write-back end to end: the block observes what Swift seeded,
// mutates it, and the mutation is visible to Swift after the call returns. Types other
// than a dictionary are deliberately present — the bridge is keyed on `inout`, not on any
// one carrier — as are a read-only sibling parameter and a second `inout` in the same
// signature.

import Foundation

/// Frozen struct carried through an `inout` closure parameter.
@frozen
public struct BuilderViewport {
    public var width: Int32
    public var height: Int32

    public init(width: Int32, height: Int32) {
        self.width = width
        self.height = height
    }
}

/// Raw-valued no-payload enum carried through an `inout` closure parameter. Two facts about
/// it have to survive the round trip: Swift stores a three-case enum in a single byte as a
/// case TAG (0/1/2), while the value the binding exposes is the RAW value — and the raw
/// values here are deliberately nowhere near the tags, so a tag delivered in place of a raw
/// value is a wrong answer rather than a coincidence.
///
/// `@objc` is load-bearing, for the same reason it is on `AuthErrorCodeLike`: the Swift
/// compiler preserves explicit enum raw values in the textual `.swiftinterface` — the
/// generator's source of truth — only for `@objc` enums. A plain `enum: Int32` has its
/// `= 4100` stripped to a bare `case queued`, so the binding can only fall back to
/// declaration-order ordinals for the C# member while the Swift side still marshals
/// `.rawValue`; that pairing is the divergence recorded in `not-planned.md`, not a shape
/// this fixture can assert on.
@objc public enum BuilderStage: Int {
    case queued = 4100
    case running = 4200
    case settled = 4300
}

/// Tag-only sibling: no raw value at all, so the case tag is the only representation and the
/// scalar the boundary carries is a widened copy of it. The other half of the enum carrier.
public enum BuilderPhase {
    case start
    case middle
    case end
}

/// Host mirroring the option-builder family: static factories whose only configuration
/// hook is a block receiving the option bag by reference.
public final class OptionsBuilderHost {
    public init() {}

    /// The reported shape verbatim — a defaulted, non-escaping block over an option bag.
    public static func showMapOptions(_ block: (inout [String: Any]) -> Void = { _ in }) -> [String: Any] {
        var options: [String: Any] = ["mode": "map"]
        block(&options)
        return options
    }

    /// Sibling builder, no default argument.
    public static func focusOptions(_ block: (inout [String: Any]) -> Void) -> [String: Any] {
        var options: [String: Any] = ["focus": "none"]
        block(&options)
        return options
    }

    /// Sibling builder taking an ordinary read-only parameter beside the block.
    public static func animationOptions(named name: String, _ block: (inout [String: Any]) -> Void) -> [String: Any] {
        var options: [String: Any] = ["animation": name]
        block(&options)
        return options
    }

    /// Instance-member sibling: the receiver rides alongside the block.
    public func markerOptions(_ block: (inout [String: Any]) -> Void) -> [String: Any] {
        var options: [String: Any] = ["marker": "pin"]
        block(&options)
        return options
    }

    /// Nested namespace enum, the exact nesting the reported family uses.
    public enum Builders {
        public static func directionsOptions(_ block: (inout [String: Any]) -> Void = { _ in }) -> [String: Any] {
            var options: [String: Any] = ["directions": "walking"]
            block(&options)
            return options
        }
    }
}

/// `inout` carriers other than a dictionary, so the bridge cannot be a dictionary
/// special case.
public final class InOutClosureCarriers {
    public init() {}

    /// Blittable primitive by reference.
    public func adjustCount(_ block: (inout Int32) -> Void) -> Int32 {
        var count: Int32 = 7
        block(&count)
        return count
    }

    /// Frozen struct by reference.
    public func adjustViewport(_ block: (inout BuilderViewport) -> Void) -> BuilderViewport {
        var viewport = BuilderViewport(width: 320, height: 480)
        block(&viewport)
        return viewport
    }

    /// Memory-managed value type by reference.
    public func adjustTitle(_ block: (inout String) -> Void) -> String {
        var title = "seed"
        block(&title)
        return title
    }

    /// Array by reference — the other COW container.
    public func adjustTags(_ block: (inout [String]) -> Void) -> [String] {
        var tags = ["one"]
        block(&tags)
        return tags
    }

    /// Raw-valued no-payload enum by reference. Returns the raw value so the C# side can tell
    /// a raw value apart from a case tag, and the seed is the MIDDLE case so a cell that never
    /// got written back reads as a distinguishable value rather than the first case.
    public func advanceStage(_ block: (inout BuilderStage) -> Void) -> Int32 {
        var stage = BuilderStage.running
        block(&stage)
        return Int32(stage.rawValue)
    }

    /// Tag-only enum by reference. Reported as a number the switch derives, since the enum has
    /// no raw value of its own to report.
    public func advancePhase(_ block: (inout BuilderPhase) -> Void) -> Int32 {
        var phase = BuilderPhase.middle
        block(&phase)
        switch phase {
        case .start: return 10
        case .middle: return 20
        case .end: return 30
        }
    }

    /// Two `inout` parameters plus a by-value sibling in one signature.
    public func adjustPair(_ block: (inout Int32, Int32, inout Int32) -> Void) -> Int32 {
        var first: Int32 = 1
        var second: Int32 = 2
        block(&first, 10, &second)
        return first * 100 + second
    }

    /// The block's own return value alongside an `inout` parameter.
    public func adjustAndReport(_ block: (inout Int32) -> Bool) -> Int32 {
        var value: Int32 = 5
        let accepted = block(&value)
        return accepted ? value : -value
    }

    /// Escaping variant: the block is stored and driven after the call that installed it.
    private var stored: ((inout Int32) -> Void)?

    public func installAdjuster(_ block: @escaping (inout Int32) -> Void) {
        stored = block
    }

    public func runInstalledAdjuster(seed: Int32) -> Int32 {
        var value = seed
        stored?(&value)
        return value
    }
}

/// Reference cell carried by the control below.
public final class BuilderBox {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }
}

/// Controls that must STAY refused, so the admitted carrier set is pinned from both
/// sides. A reference cell is a slot Swift load/stores and releases the displaced object
/// through, which the projection (carrying the object, not the slot) cannot honour; an
/// `Optional` is read back out of the cell by a discriminator that is spelled differently
/// per flavour, which the address-based read does not recover.
public final class InOutClosureRefusedCarriers {
    public init() {}

    public func adjustBox(_ block: (inout BuilderBox) -> Void) -> Int32 {
        var box = BuilderBox(value: 3)
        block(&box)
        return box.value
    }

    public func adjustMaybe(_ block: (inout Int32?) -> Void) -> Int32 {
        var maybe: Int32? = 4
        block(&maybe)
        return maybe ?? -1
    }
}
