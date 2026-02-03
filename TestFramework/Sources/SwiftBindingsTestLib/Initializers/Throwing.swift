// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Initializer Error

/// Error type for ValidatedConfig.
public enum ConfigError: Error {
    case invalidTimeout
    case emptyName
}

// MARK: - Throwing Initializer

/// Struct with a throwing initializer.
public struct ValidatedConfig {
    public let name: String
    public let timeout: Int32

    /// Throwing init: throws if timeout is negative or name is empty.
    public init(name: String, timeout: Int32) throws {
        guard !name.isEmpty else {
            throw ConfigError.emptyName
        }
        guard timeout >= 0 else {
            throw ConfigError.invalidTimeout
        }
        self.name = name
        self.timeout = timeout
    }
}
