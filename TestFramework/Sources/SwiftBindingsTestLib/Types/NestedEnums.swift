// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested Enums in Class (CryptoSwift AES.Variant pattern)

/// Class with nested enum types — tests scoping of enums inside class (vs struct).
/// Real-world pattern: CryptoSwift AES.Variant, SHA2.Variant; Alamofire URLEncoding.Destination.
public class Codec {
    public enum Format: Int32 {
        case json = 0
        case xml = 1
        case binary = 2
    }

    public enum Encoding: String {
        case utf8 = "utf-8"
        case ascii = "ascii"
        case latin1 = "latin-1"
    }

    /// Nested enum with associated values.
    public enum CompressionLevel {
        case none
        case fast
        case best
        case custom(level: Int32)
    }

    public var format: Format
    public var encoding: Encoding

    public init(format: Format, encoding: Encoding) {
        self.format = format
        self.encoding = encoding
    }

    public func describe() -> String { "\(format) / \(encoding.rawValue)" }
}

// MARK: - Enum with Enum-Typed Associated Value (CryptoSwift HMAC pattern)

/// Standalone enum for use as associated value type.
public enum SHA2Variant: Int32 {
    case sha224 = 0
    case sha256 = 1
    case sha384 = 2
    case sha512 = 3
}

/// Enum with enum-typed associated value.
/// Real-world pattern: CryptoSwift HMAC.Variant.Sha2(SHA2.Variant.Sha256).
public enum HashAlgorithm {
    case md5
    case sha1
    case sha2(variant: SHA2Variant)
    case custom(rounds: Int32)
}

/// Factory function for creating SHA2 hash algorithm.
public func createHashAlgorithm(sha2Variant: SHA2Variant) -> HashAlgorithm {
    return .sha2(variant: sha2Variant)
}

/// Describe function for hash algorithm.
public func describeAlgorithm(_ algo: HashAlgorithm) -> String {
    switch algo {
    case .md5: return "MD5"
    case .sha1: return "SHA1"
    case .sha2(let v): return "SHA2-\(v.rawValue)"
    case .custom(let r): return "Custom-\(r)"
    }
}

// MARK: - L2: Nested Enum-with-AVs inside Enum-with-AVs (Lottie PlaybackMode pattern)

/// Enum containing a nested enum, both with associated values.
public enum PlaybackMode {
    case playing(speed: Double)
    case paused(reason: PauseReason)
    case stopped

    public enum PauseReason {
        case userAction
        case buffering(progress: Double)
        case error(code: Int32)
    }
}

public func describePlaybackMode(_ mode: PlaybackMode) -> String {
    switch mode {
    case .playing(let speed): return "Playing at \(speed)x"
    case .paused(let reason):
        switch reason {
        case .userAction: return "Paused by user"
        case .buffering(let progress): return "Buffering: \(progress)%"
        case .error(let code): return "Error: \(code)"
        }
    case .stopped: return "Stopped"
    }
}

// MARK: - L6: Nested Enum with String RawValue + CaseIterable

extension Codec {
    public enum Alignment: String, CaseIterable {
        case left = "left"
        case center = "center"
        case right = "right"
    }
}
