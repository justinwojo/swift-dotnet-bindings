// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Struct with Multiple Init Overloads

/// Struct with multiple initializer overloads.
@frozen
public struct BasicInit {
    public let x: Int32
    public let y: Int32

    /// Default initializer.
    public init() {
        self.x = 0
        self.y = 0
    }

    /// Initializer with one parameter.
    public init(value: Int32) {
        self.x = value
        self.y = value
    }

    /// Initializer with two parameters.
    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }
}

// MARK: - Class with Designated and Convenience Init

/// Class with designated and convenience initializers.
public class ConvenienceInit {
    public let name: String
    public let value: Int32

    /// Designated initializer.
    public init(name: String, value: Int32) {
        self.name = name
        self.value = value
    }

    /// Convenience initializer with just a name.
    public convenience init(name: String) {
        self.init(name: name, value: 0)
    }

    /// Convenience initializer with just a value.
    public convenience init(value: Int32) {
        self.init(name: "unnamed", value: value)
    }

    /// Convenience initializer with no arguments.
    public convenience init() {
        self.init(name: "default", value: -1)
    }
}
