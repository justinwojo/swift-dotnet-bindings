// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Static Struct Singleton (Alamofire URLEncoding.Default pattern)

/// Struct with static let properties returning Self.
/// Real-world pattern: Alamofire URLEncoding.Default, Kingfisher DefaultImageProcessor.Default.
public struct EncodingConfig {
    public var formatName: String
    public var maxLength: Int32

    public init(formatName: String, maxLength: Int32) {
        self.formatName = formatName
        self.maxLength = maxLength
    }

    public static let standard = EncodingConfig(formatName: "standard", maxLength: 1024)
    public static let compact = EncodingConfig(formatName: "compact", maxLength: 256)
    public static let minimal = EncodingConfig(formatName: "minimal", maxLength: 64)

    public func isWithinLimit(_ length: Int32) -> Bool { length <= maxLength }
}
