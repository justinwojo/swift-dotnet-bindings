// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

/// A >3-word Equatable value with both heap-backed String and tracked ARC ownership.
/// Exercises the runtime's generic equality helper, independently of generated == wrappers.
@frozen
public struct RuntimeEquatableValue: Equatable {
    public let text: String
    public let token: TrackedRef
    public let first: Int64
    public let last: Int64

    public init(text: String, tag: Int32, first: Int64, last: Int64) {
        self.text = text
        self.token = TrackedRef(tag: tag, category: "RuntimeEquatableValue")
        self.first = first
        self.last = last
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.text == rhs.text && lhs.token.tag == rhs.token.tag &&
            lhs.first == rhs.first && lhs.last == rhs.last
    }
}
