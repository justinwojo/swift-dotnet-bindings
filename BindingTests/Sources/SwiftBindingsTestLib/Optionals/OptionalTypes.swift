// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Blittable Return

/// Finds the index of a value in the array, or nil if not found.
public func findIndex(_ array: [Int32], value: Int32) -> Int32? {
    if let idx = array.firstIndex(of: value) {
        return Int32(idx)
    }
    return nil
}

// MARK: - Optional Class Return

/// Finds an animal by name from the array, or nil if not found.
public func findAnimalByName(_ animals: [Animal], name: String) -> Animal? {
    return animals.first { $0.name == name }
}

// MARK: - Optional Parameter

/// Describes an optional Int32, returning "nil" or the string value.
public func describeOptionalInt(_ value: Int32?) -> String {
    if let v = value {
        return "Value: \(v)"
    }
    return "nil"
}

// MARK: - Optional String Parameter

/// Describes an optional String, returning "nil" or the value.
public func describeOptionalString(_ value: String?) -> String {
    if let v = value {
        return "Value: \(v)"
    }
    return "nil"
}

// MARK: - Optional String Return (optbuf regression)

/// Returns an optional string that exceeds SSO (>15 UTF-8 bytes) to exercise
/// the optbuf return wrapper path with ARC-managed heap strings.
/// This reproduces the DeviceKit Device.name / PhoneNumberKit MainCountry crash
/// where copyMemory (raw memcpy) caused use-after-free on the returned string.
public func getLongOptionalString(_ returnNil: Bool) -> String? {
    if returnNil { return nil }
    return "This is a long string that exceeds small string optimization"
}

/// Same as above but on a non-frozen class to exercise the class property optbuf path.
public class OptionalStringHolder {
    private let _value: String?

    public init(value: String?) {
        self._value = value
    }

    /// Returns Optional<String> via property getter — exercises optbuf wrapper for class properties.
    public var optionalName: String? {
        return _value
    }
}

// MARK: - Struct with Optional Properties

/// A frozen struct with optional properties for testing optional field emission.
@frozen
public struct OptionalConfig {
    public var label: String?
    public var count: Int32?
    public var fallbackLabel: String

    public init(label: String?, count: Int32?, fallbackLabel: String) {
        self.label = label
        self.count = count
        self.fallbackLabel = fallbackLabel
    }

    /// Returns the effective label (label or fallbackLabel).
    public func effectiveLabel() -> String {
        return label ?? fallbackLabel
    }
}
