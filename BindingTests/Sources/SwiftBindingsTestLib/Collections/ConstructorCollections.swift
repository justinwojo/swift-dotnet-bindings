// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Constructor with Int32 Array (crypto-HMAC / animation-keypath pattern)

/// Class taking array in constructor.
/// Real-world pattern: HMAC(byte[]), AnimationKeypath(List<string>).
public class DataBuffer {
    private let data: [Int32]

    public init(data: [Int32]) {
        self.data = data
    }

    public func count() -> Int32 { Int32(data.count) }
    public func sum() -> Int32 { data.reduce(0, +) }
    public func first() -> Int32? { data.first }
}

// MARK: - Constructor with String Array (animation-keypath pattern)

/// Class taking string array in constructor.
public class PathResolver {
    private let components: [String]

    public init(components: [String]) {
        self.components = components
    }

    public func fullPath() -> String { components.joined(separator: ".") }
    public func depth() -> Int32 { Int32(components.count) }
}

// MARK: - Constructor with Array + Other Params

/// Class taking string + array in constructor.
public class LabeledBuffer {
    private let label: String
    private let data: [Int32]

    public init(label: String, data: [Int32]) {
        self.label = label
        self.data = data
    }

    public func describe() -> String { "\(label): \(data.count) items" }
}
