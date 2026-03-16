// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Static Factory Returning Optional (Lottie LottieAnimation.Filepath pattern)

/// Class with static factory methods returning optional Self.
/// Real-world pattern: Lottie LottieAnimation.Filepath() -> LottieAnimation?.
public class ConfigLoader {
    public let name: String
    public let version: Int32

    private init(name: String, version: Int32) {
        self.name = name
        self.version = version
    }

    /// Factory returns nil for empty name.
    public static func create(name: String) -> ConfigLoader? {
        guard !name.isEmpty else { return nil }
        return ConfigLoader(name: name, version: 1)
    }

    /// Factory returns nil for invalid version.
    public static func create(name: String, version: Int32) -> ConfigLoader? {
        guard version > 0 else { return nil }
        return ConfigLoader(name: name, version: version)
    }

    public func describe() -> String { "\(name) v\(version)" }
}
