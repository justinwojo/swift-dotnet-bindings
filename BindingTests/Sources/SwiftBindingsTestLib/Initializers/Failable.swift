// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Failable Initializers (S1)
// Tests: init? projected as TryCreate with out param

/// Struct with a failable initializer (division by zero guard).
@frozen
public struct SafeDiv {
    public let numerator: Int32
    public let denominator: Int32
    public let result: Double

    /// Failable init: returns nil if denominator is zero.
    public init?(numerator: Int32, denominator: Int32) {
        guard denominator != 0 else { return nil }
        self.numerator = numerator
        self.denominator = denominator
        self.result = Double(numerator) / Double(denominator)
    }
}

/// Non-frozen struct with a failable initializer.
/// Tests TryCreate with `result = default!` for class-projected structs (CS8625 fix).
public struct NonEmptyString {
    public let value: String
    public let length: Int32

    /// Failable init: returns nil if the string is empty.
    public init?(_ string: String) {
        guard !string.isEmpty else { return nil }
        self.value = string
        self.length = Int32(string.count)
    }
}

/// Struct with a range-validated initializer.
@frozen
public struct RangedInt {
    public let value: Int32
    public let min: Int32
    public let max: Int32

    /// Failable init: returns nil if value is outside [min, max].
    public init?(value: Int32, min: Int32, max: Int32) {
        guard value >= min && value <= max else { return nil }
        self.value = value
        self.min = min
        self.max = max
    }
}

// MARK: - Failable CLASS Initializers (reference-type return convention)
// A failable initializer on a *reference type* differs fundamentally from the struct
// cases above. The allocating initializer returns Optional<Self> as a SINGLE nullable
// class pointer directly in the result register (nil == failure) — it does NOT use an
// indirect result buffer the way a failable value-type init does. The @_cdecl wrapper
// therefore returns `UnsafeMutableRawPointer?` directly and the projected TryCreate
// reads a plain IntPtr (IntPtr.Zero == failure), with no leading resultPtr arg-shift.

/// Tracking preference enum mirroring a real-world login-configuration option.
public enum TrackingPreference: Int32 {
    case enabled = 0
    case limited = 1
    case disabled = 2
}

/// Pure-Swift reference type (class) with a failable initializer that takes a collection
/// and an enum. Exercises the wrapped failable-class path: the collection param forces a
/// @_cdecl wrapper, the init
/// returns the retained instance pointer directly, and Swift-native ARC (swift_release)
/// governs the returned object's lifetime.
public class AccessConfiguration {
    public let permissions: [String]
    public let tracking: TrackingPreference

    /// Failable init: returns nil when no permissions are requested.
    public init?(permissions: [String], tracking: TrackingPreference) {
        guard !permissions.isEmpty else { return nil }
        self.permissions = permissions
        self.tracking = tracking
    }

    public var permissionCount: Int32 { Int32(permissions.count) }

    public func permission(at index: Int32) -> String { permissions[Int(index)] }
}

/// NSObject-rooted reference type with a failable initializer.
/// Same direct nullable-pointer return convention as AccessConfiguration, but the returned
/// instance is ObjC-rooted, so its lifetime is governed by ObjC ARC (objc_release /
/// DangerousRelease) rather than swift_release. Confirms the wrapped failable-class path
/// round-trips for both release flavors.
public class NamedToken: NSObject {
    @objc public let label: String

    /// Failable init: returns nil for an empty label.
    public init?(label: String) {
        guard !label.isEmpty else { return nil }
        self.label = label
        super.init()
    }
}
