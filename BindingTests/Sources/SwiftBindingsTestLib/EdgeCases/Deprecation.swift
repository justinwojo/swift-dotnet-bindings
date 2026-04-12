// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Availability and Deprecation Tests

/// Struct with deprecated and version-gated members.
public struct DeprecationTest {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Normal method (always available).
    public func normalMethod() -> Int32 {
        return value
    }

    /// Deprecated method — should emit [Obsolete] or equivalent in C#.
    @available(*, deprecated, message: "Use normalMethod instead")
    public func oldMethod() -> Int32 {
        return value
    }

    /// Deprecated with renamed replacement.
    @available(*, deprecated, renamed: "normalMethod")
    public func legacyMethod() -> Int32 {
        return value
    }

    /// Version-gated method (iOS 16+).
    @available(iOS 16.0, *)
    public func modernMethod() -> Int32 {
        return value * 2
    }

    /// Unavailable method — should be excluded from bindings entirely.
    @available(*, unavailable, message: "This method is not available")
    public func unavailableMethod() -> Int32 {
        return value
    }
}

// MARK: - Version-Gated Free Functions

/// Function available only on iOS 16+.
@available(iOS 16.0, *)
public func modernFunction() -> String {
    return "modern"
}

/// Deprecated free function.
@available(*, deprecated, message: "Use modernFunction instead")
public func legacyFunction() -> String {
    return "legacy"
}

// MARK: - Per-Accessor Availability (Setter tighter than Getter)
//
// Mirrors the real WorkoutKit.PowerThresholdAlert.metric shape where the getter is
// available from iOS 17.0 but the setter is iOS 17.4. The generator must (1) emit
// a stricter Swift @_cdecl wrapper for the setter, and (2) emit an accessor-level
// [SupportedOSPlatform("ios17.4")] on the C# set accessor so consumers cannot call
// it under the looser getter floor. Without both, the generated Swift wrapper will
// try to reference a 17.4-only symbol under the 17.0 guard and fail to compile.

@available(iOS 17.0, *)
public struct SetterTighterThanGetter {
    @usableFromInline internal var _storage: Int32 = 0

    public init(initial: Int32) { self._storage = initial }

    public var value: Int32 {
        get { _storage }
        @available(iOS 17.4, *)
        set { _storage = newValue }
    }
}
