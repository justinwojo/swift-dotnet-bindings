// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Property Wrapper Definition

/// A property wrapper that clamps a value to a given range.
/// Tests: @propertyWrapper struct, wrappedValue, projectedValue ($prop).
/// Expected C#: Clamped<T> type with wrappedValue and projectedValue properties.
@propertyWrapper
public struct Clamped {
    private var value: Int32
    private let range: ClosedRange<Int32>

    public var wrappedValue: Int32 {
        get { return value }
        set { value = min(max(newValue, range.lowerBound), range.upperBound) }
    }

    /// Projected value indicates whether the value was clamped.
    public var projectedValue: Bool {
        return value == range.lowerBound || value == range.upperBound
    }

    public init(wrappedValue: Int32, min: Int32, max: Int32) {
        self.range = min...max
        self.value = Swift.min(Swift.max(wrappedValue, min), max)
    }
}

// MARK: - Type Using Property Wrapper

/// Struct that uses the Clamped property wrapper.
/// Expected C#: Properties for the wrapped value, plus $-prefixed projected values.
public struct ClampedSettings {
    @Clamped(min: 0, max: 100) public var volume: Int32 = 50
    @Clamped(min: 1, max: 10) public var brightness: Int32 = 5

    public init() {}

    public init(volume: Int32, brightness: Int32) {
        self.volume = volume
        self.brightness = brightness
    }

    /// Returns current settings as a string.
    public var summary: String {
        return "Volume: \(volume), Brightness: \(brightness)"
    }
}

// MARK: - Simple Property Wrapper (Non-Generic)

/// A property wrapper that trims whitespace from strings.
@propertyWrapper
public struct Trimmed {
    private var value: String

    public var wrappedValue: String {
        get { return value }
        set { value = newValue.trimmingCharacters(in: .whitespacesAndNewlines) }
    }

    public init(wrappedValue: String) {
        self.value = wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

// MARK: - Type Using Trimmed Wrapper

/// Struct that uses the Trimmed property wrapper.
public struct UserInput {
    @Trimmed public var username: String = ""
    @Trimmed public var email: String = ""

    public init() {}

    public init(username: String, email: String) {
        self.username = username
        self.email = email
    }
}

// MARK: - Free Functions

/// Creates a ClampedSettings with the given values (values will be clamped).
public func createClampedSettings(volume: Int32, brightness: Int32) -> ClampedSettings {
    return ClampedSettings(volume: volume, brightness: brightness)
}

/// Creates a UserInput with the given strings (strings will be trimmed).
public func createUserInput(username: String, email: String) -> UserInput {
    return UserInput(username: username, email: email)
}
