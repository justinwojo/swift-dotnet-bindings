// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested types declared in constrained extensions of a generic type
//
// A type declared inside `extension Box where Value == Concrete { … }` is a member of
// the *generic* parent, so its Swift spelling needs the parent's argument:
// `PinBox<PinTag>.Op`, `PinBox<PinTag>.Op.Options`. Two spellings reach the generator:
//
//   * pinned — the extension fixes the parameter (`where Value == PinTag`). Every
//     wrapper must name the nested type with the pinned argument; `PinBox.Op` alone
//     does not type-check, which withdrew whole families of members (RealityKit's
//     `SampledAnimation<…>.SkeletalAnimationOperation.RootMotionOptions`).
//   * unpinned — the parameter stays open (`where Value: PinMeasured`, or no
//     constraint at all). A `@_cdecl` cannot be generic, so the raw-value support of
//     such an enum (`FromRawValue`, its case statics) must reach `Self` through the
//     enum's metadata (AppIntents' `IntentParameter.DurationUnit`).
//
// Both shapes must bind with their members, and those members must run.

/// Generic carrier whose constrained extensions declare the nested types below.
public struct PinBox<Value> {
    public init() {}
}

/// The argument the pinned extension fixes.
public struct PinTag {
    public init() {}
}

/// Pinned spelling: a struct and an OptionSet nested two levels under
/// `PinBox<PinTag>`.
extension PinBox where Value == PinTag {
    public struct Op {
        public struct Options: OptionSet {
            public let rawValue: UInt16
            public init(rawValue: UInt16) { self.rawValue = rawValue }
            public static let first = Options(rawValue: 1)
            public static let second = Options(rawValue: 2)
        }

        public let label: Int32
        public init() { label = 41 }

        /// Reads through the pinned parent; a constrained-extension accessor.
        public var tag: Int32 { label + 1 }
    }
}

/// Builds the nested values from Swift, so a test can reach them on every engine.
public func makePinOp() -> PinBox<PinTag>.Op {
    return PinBox<PinTag>.Op()
}

public func makePinOptions(_ rawValue: UInt16) -> PinBox<PinTag>.Op.Options {
    return PinBox<PinTag>.Op.Options(rawValue: rawValue)
}

/// Reports the raw value of an option set built on the C# side.
public func pinOptionsRawValue(_ options: PinBox<PinTag>.Op.Options) -> UInt16 {
    return options.rawValue
}

/// A protocol with an associated type the unpinned extension constrains.
public protocol PinMeasured {
    associatedtype Unit
}

/// Satisfies the unpinned extension's constraint (`Unit == Double`).
public struct PinMeter: PinMeasured {
    public typealias Unit = Double
    public init() {}
}

/// Unpinned spelling under a constraint: a String raw-value enum whose parent keeps
/// its generic parameter.
extension PinBox where Value: PinMeasured, Value.Unit == Double {
    public enum Unit: String {
        case seconds
        case minutes
    }
}

/// Unpinned spelling with no constraint at all: the nested enum of a plain generic
/// struct, with an integer raw value.
public struct OpenBox<Value> {
    public init() {}

    public enum Kind: Int32 {
        case first = 3
        case second = 7
    }

    /// A frozen enum's init(rawValue:) returns its Optional in the enum's own lowering (one
    /// discriminator byte here), so the binding must not read that result as a pointer.
    @frozen
    public enum Stage: Int32 {
        case early = 3
        case late = 9
    }
}

/// Reports the raw value Swift reads back from a nested enum value built on the C# side.
public func pinUnitRawValue(_ unit: PinBox<PinMeter>.Unit) -> String {
    return unit.rawValue
}

public func openKindRawValue(_ kind: OpenBox<PinTag>.Kind) -> Int32 {
    return kind.rawValue
}

public func openStageRawValue(_ stage: OpenBox<PinTag>.Stage) -> Int32 {
    return stage.rawValue
}
