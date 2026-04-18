// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Fixture for the ExistentialBypassEmitter non-void return path. Each method has an
// omittable existential-in-bound-generic parameter (`[any Equatable] = []`) AND returns
// `any Describable`. Without the void-return gate lift, these skip entirely.

public class ExistentialReturnBypassHost {
    private let stored: SimpleItem

    public init(label: String) {
        self.stored = SimpleItem(id: "erb", label: label)
    }

    // Instance method on class returning `any Describable`, with an omittable existential
    // param (Array<any Equatable> has default []). Hits the class branch of the bypass.
    public func makeItem(_ filters: [any Equatable] = []) -> any Describable {
        _ = filters
        return stored
    }
}

public struct ExistentialReturnBypassStructHost {
    private let label: String
    private var handle: SimpleItem

    public init(label: String) {
        self.label = label
        self.handle = SimpleItem(id: "erb-s", label: label)
    }

    // Non-frozen struct method returning an existential. Drives the struct branch
    // (self passed via UnsafeMutableRawPointer → pointee).
    public func makeItem(_ filters: [any Equatable] = []) -> any Describable {
        _ = filters
        return handle
    }
}
