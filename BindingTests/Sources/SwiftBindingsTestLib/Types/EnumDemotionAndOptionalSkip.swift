// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Regression coverage for two emission regressions surfaced by the real-world
// library sweep:
//
//   1. A NON-frozen struct with two sub-word Optional<primitive> stored properties
//      (`Bool?`) was wrongly classified as having a "sub-word Optional layout
//      mismatch" and added to the type-skip pre-pass set, which silently dropped its
//      own initializer and static factories even though the struct still emitted.
//      A non-frozen struct is projected as an opaque pointer-passed class and never
//      lowers through a by-value ABI, so the by-value layout risk cannot apply to it.
//
//   2. A raw-value enum that conforms to a protocol and is used as a generic argument
//      at a marker-constrained position (`where T : Sendable`) regressed from a
//      protocol-implementing class to a bare C# enum after the parser began dropping
//      the module-qualified marker. Dropping the marker erased the only signal the
//      enum-demotion gate keyed off, so the enum was no longer demoted and could no
//      longer implement the protocol's projected interface.

// MARK: - Non-frozen struct with sub-word Optional stored properties

/// Non-frozen struct whose two `Bool?` stored properties pack into sub-word Optional
/// layout. Its initializer and static factories must survive emission.
public struct ToggleOptions {
    public var primaryEnabled: Bool?
    public var secondaryEnabled: Bool?

    public init(primaryEnabled: Bool?, secondaryEnabled: Bool?) {
        self.primaryEnabled = primaryEnabled
        self.secondaryEnabled = secondaryEnabled
    }

    /// Static factory returning a fully-enabled instance.
    public static func allOn() -> ToggleOptions {
        return ToggleOptions(primaryEnabled: true, secondaryEnabled: true)
    }

    /// Static factory returning an instance with a nil primary flag.
    public static func defaults() -> ToggleOptions {
        return ToggleOptions(primaryEnabled: nil, secondaryEnabled: false)
    }
}

// MARK: - Protocol-conforming raw-value enum used at a marker-constrained position

/// Simple requirement projected to a C# interface the demoted enum must implement.
public protocol TagValueProviding {
    var tagValue: Int32 { get }
}

/// Raw-value enum that also conforms to a protocol. To implement the projected
/// interface it must be demoted to a class (a C# enum cannot implement an interface).
public enum AlertKind: Int32, TagValueProviding {
    case info = 0
    case warning = 1
    case critical = 2

    public var tagValue: Int32 {
        return self.rawValue
    }
}

/// Generic container whose type parameter carries only a marker constraint. Using the
/// enum here is what drives the demotion gate; the marker is dropped at parse time but
/// the "position is constrained" signal must survive so demotion still fires.
public struct SendableBox<T: Sendable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }
}

/// Surfaces `SendableBox<AlertKind>` in a scanned member position so the enum is seen
/// as used at a marker-constrained generic argument slot.
public struct AlertCarrier {
    public let boxed: SendableBox<AlertKind>

    public init(kind: AlertKind) {
        self.boxed = SendableBox(item: kind)
    }
}
