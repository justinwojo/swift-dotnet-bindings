// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Base Class

/// Base class for testing class hierarchy emission.
open class Animal {
    public var name: String
    public var sound: String

    public init(name: String, sound: String) {
        self.name = name
        self.sound = sound
    }

    /// Instance method.
    open func speak() -> String {
        return "\(name) says \(sound)"
    }

    /// Method that can be overridden.
    open func describe() -> String {
        return "Animal: \(name)"
    }
}

// MARK: - Inheritance

/// Subclass testing inheritance emission.
public class Dog: Animal {
    public var breed: String

    public init(name: String, breed: String) {
        self.breed = breed
        super.init(name: name, sound: "Woof")
    }

    /// Overridden method.
    override public func describe() -> String {
        return "Dog: \(name) (\(breed))"
    }

    /// Subclass-specific method.
    public func fetch() -> String {
        return "\(name) fetches the ball!"
    }
}

// MARK: - Final Class

/// Final class — cannot be subclassed.
public final class FinalCounter {
    public var count: Int32

    public init(count: Int32 = 0) {
        self.count = count
    }

    /// Mutating instance method.
    public func increment() -> Int32 {
        count += 1
        return count
    }

    /// Mutating instance method with parameter.
    public func add(_ amount: Int32) -> Int32 {
        count += amount
        return count
    }

    /// Reset to zero.
    public func reset() {
        count = 0
    }
}

// MARK: - Class with Property Varieties

/// Class with stored, computed, and static properties.
public class ClassWithProperties {
    public var storedInt: Int32
    public var storedString: String

    public init(storedInt: Int32, storedString: String) {
        self.storedInt = storedInt
        self.storedString = storedString
    }

    /// Computed property (read-only).
    public var summary: String {
        return "\(storedString): \(storedInt)"
    }

    /// Static property.
    public static var instanceCount: Int32 = 0

    /// Static method.
    public static func resetCount() {
        instanceCount = 0
    }
}

// MARK: - Free Functions

/// Free function returning a class instance.
public func createAnimal(name: String, sound: String) -> Animal {
    return Animal(name: name, sound: sound)
}
