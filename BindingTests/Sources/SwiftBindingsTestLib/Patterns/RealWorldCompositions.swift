// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Type 1: Frozen Struct with Optional Array Property

/// Frozen struct combining optional array, blittable properties, and methods.
/// Real-world pattern: BlinkID config objects with optional collection fields.
@frozen public struct BatchConfig {
    public var name: String
    public var maxRetries: Int32
    public var tags: [Int32]?

    public init(name: String, maxRetries: Int32, tags: [Int32]?) {
        self.name = name
        self.maxRetries = maxRetries
        self.tags = tags
    }

    /// Returns the number of tags, or 0 if tags is nil.
    public func tagCount() -> Int32 {
        return Int32(tags?.count ?? 0)
    }

    /// Returns the effective name for display.
    public func effectiveName() -> String {
        return "\(name) (retries: \(maxRetries))"
    }
}

// MARK: - Type 2: Class with Inheritance + Protocol Conformance

/// Class combining inheritance (Animal) with protocol conformance (HasValue).
/// Real-world pattern: Lottie animation hierarchy implementing protocol interfaces.
public class ValueAnimal: Animal, HasValue {
    public var value: Int32

    public init(name: String, sound: String, value: Int32) {
        self.value = value
        super.init(name: name, sound: sound)
    }

    public func getValue() -> Int32 {
        return value
    }

    public func setValue(_ newValue: Int32) {
        value = newValue
    }

    /// Summary combining inherited and own properties.
    public func summary() -> String {
        return "\(name) (\(sound)) value=\(value)"
    }
}

// MARK: - Type 3: Singleton with Optional Class Return

/// Singleton class with optional class return and class parameter.
/// Real-world pattern: Nuke ImagePipeline.shared with lookup returning nil.
public final class Registry {
    public static let shared = Registry()

    private var entries: [Int32: Animal] = [:]
    private var nextId: Int32 = 0

    private init() {}

    /// Register an animal and return its assigned ID.
    public func register(_ animal: Animal) -> Int32 {
        let id = nextId
        entries[id] = animal
        nextId += 1
        return id
    }

    /// Lookup an animal by ID; returns nil if not found.
    public func lookup(id: Int32) -> Animal? {
        return entries[id]
    }

    /// Number of registered entries.
    public func count() -> Int32 {
        return Int32(entries.count)
    }

    /// Remove all entries.
    public func clear() {
        entries.removeAll()
        nextId = 0
    }
}

// MARK: - Type 4: Class with Optional Closure Property (known unsupported)

/// Class combining optional closure property with static factory.
/// Real-world pattern: Lottie animation callback handlers.
public final class EventHandler {
    public var onComplete: ((Int32) -> Bool)?
    public var label: String

    public init(label: String, onComplete: ((Int32) -> Bool)?) {
        self.label = label
        self.onComplete = onComplete
    }

    /// Create a default handler with no callback.
    public static func createDefault() -> EventHandler {
        return EventHandler(label: "default", onComplete: nil)
    }

    /// Fire the callback if present; returns false if no callback is set.
    public func fire(_ value: Int32) -> Bool {
        return onComplete?(value) ?? false
    }
}

// MARK: - Type 5: Frozen Struct with Closure Return (known unsupported)

/// Frozen struct combining closure parameter and closure return.
/// Real-world pattern: functional transform pipelines.
@frozen public struct Transformer {
    public var offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    /// Apply a transform to a value with the offset added.
    public func apply(_ value: Int32, using transform: @escaping (Int32) -> Int32) -> Int32 {
        return transform(value + offset)
    }

    /// Chain two transforms into one.
    public static func chain(_ f: @escaping (Int32) -> Int32, _ g: @escaping (Int32) -> Int32) -> (Int32) -> Int32 {
        return { x in g(f(x)) }
    }
}

// MARK: - Free Functions

/// Describe a batch config for display.
public func describeConfig(_ config: BatchConfig) -> String {
    let tagDesc = config.tags != nil ? "\(config.tags!.count) tags" : "no tags"
    return "\(config.name): \(tagDesc), max \(config.maxRetries) retries"
}

/// Process a registry and return the total count.
public func processRegistry(_ registry: Registry) -> Int32 {
    return registry.count()
}
