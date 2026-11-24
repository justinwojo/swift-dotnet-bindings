// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A bare `Any` crosses as an ExistentialContainer0, which C# unboxes into a plain value. The
// unbox only copies the value out, so wherever Swift hands the container over at +1 (a return,
// an enum copy, an Optional, an async result, a stored property) the read must also destroy it. Each factory below puts a `TrackedRef` in the `Any`: ExistentialContainer0
// cannot unbox a class, so the C# read throws, and the leak probe checks that the container was
// released all the same. The string factories are the positive controls of the same shapes.

/// Enum payloads typed `Any`, alone, labeled beside another value, and wrapped in an Optional.
public enum TrackedAnyPayload {
    case single(Any)
    case labeled(name: String, value: Any)
    case optional(Any?)
}

public func makeTrackedAnyPayloadSingle(tag: Int32) -> TrackedAnyPayload {
    .single(TrackedRef(tag: tag))
}

public func makeTrackedAnyPayloadLabeled(tag: Int32) -> TrackedAnyPayload {
    .labeled(name: "n\(tag)", value: TrackedRef(tag: tag))
}

public func makeTrackedAnyPayloadOptional(tag: Int32) -> TrackedAnyPayload {
    .optional(TrackedRef(tag: tag))
}

public func makeStringAnyPayloadOptional(_ text: String) -> TrackedAnyPayload {
    .optional(text)
}

/// A bare `Any` returned directly, as an Optional, and from an async function.
public func makeTrackedAny(tag: Int32) -> Any {
    TrackedRef(tag: tag)
}

public func makeTrackedOptionalAny(tag: Int32) -> Any? {
    TrackedRef(tag: tag)
}

public func makeStringOptionalAny(_ text: String) -> Any? {
    text
}

public func makeTrackedAnyAsync(tag: Int32) async -> Any {
    TrackedRef(tag: tag)
}

public func makeTrackedOptionalAnyAsync(tag: Int32) async -> Any? {
    TrackedRef(tag: tag)
}

public func makeStringOptionalAnyAsync(_ text: String) async -> Any? {
    text
}

public func makeNoOptionalAnyAsync() async -> Any? {
    nil
}

/// A stored `Any` property read through its getter.
public final class TrackedAnyHolder {
    public var value: Any

    public init(tag: Int32) {
        value = TrackedRef(tag: tag)
    }
}
