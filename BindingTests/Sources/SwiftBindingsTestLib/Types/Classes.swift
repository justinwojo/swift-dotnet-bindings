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

// MARK: - Optional Class Constructor

/// Class with optional parent reference.
public class TreeNode {
    public let label: String
    public let parent: TreeNode?

    public init(label: String, parent: TreeNode?) {
        self.label = label
        self.parent = parent
    }

    public func depth() -> Int32 {
        if let p = parent { return p.depth() + 1 }
        return 0
    }

    public func rootLabel() -> String {
        if let p = parent { return p.rootLabel() }
        return label
    }
}

// MARK: - Multiple Class Singletons

/// Class with multiple static let singleton instances.
public class Scope {
    public let name: String

    private init(name: String) {
        self.name = name
    }

    public static let transient = Scope(name: "transient")
    public static let graph = Scope(name: "graph")
    public static let container = Scope(name: "container")
    public static let weak = Scope(name: "weak")

    public func describe() -> String { "Scope: \(name)" }
}

// MARK: - Q1: 3+ Level Class Hierarchy (SVGNode→SVGShape→SVGCircle pattern)

/// Third-level subclass testing deep inheritance dispatch.
public class Puppy: Dog {
    public var toyName: String

    public init(name: String, breed: String, toyName: String) {
        self.toyName = toyName
        super.init(name: name, breed: breed)
    }

    override public func describe() -> String {
        return "Puppy: \(name) (\(breed)), toy=\(toyName)"
    }

    public func play() -> String {
        return "\(name) plays with \(toyName)"
    }
}

// MARK: - Y2: Class with No Public Init

/// Class obtainable only via factory — no public constructors emitted.
/// Tests @_hasMissingDesignatedInitializers behavior.
public class Token {
    public let value: String

    init(value: String) {
        self.value = value
    }

    public func describe() -> String {
        return "Token(\(value))"
    }
}

/// Factory function for creating Token instances.
public func createToken(value: String) -> Token {
    return Token(value: value)
}

// MARK: - Nested Class with Property Name Collision

/// Class with a nested class whose name collides with a property name.
/// When PascalCased, the property `animator` becomes `Animator` — the same as
/// the nested class name. The generator must rename the type to `AnimatorInfo`
/// and update ALL references including SwiftClassHandle<T> generic parameters.
public class ImageTransitionTest {
    public final class Animator {
        public var isActive: Bool

        public init(isActive: Bool) {
            self.isActive = isActive
        }

        public func status() -> String {
            return isActive ? "active" : "inactive"
        }
    }

    public var animator: Animator

    public init(animator: Animator) {
        self.animator = animator
    }

    public func describe() -> String {
        return "Transition with \(animator.status()) animator"
    }
}

// MARK: - Weak and Unowned References
// NOTE: Temporarily disabled. Generator bug with tuples containing class types
// (tries to access Owner.Buffer which doesn't exist for pure reference types).

// /// Owner class used as a target for weak and unowned references.
// public class Owner {
//     public var name: String
//
//     public init(name: String) {
//         self.name = name
//     }
// }
//
// /// Holds a weak reference to an Owner. The reference becomes nil when the owner is deallocated.
// public class WeakReferenceHolder {
//     public weak var owner: Owner?
//
//     public init(owner: Owner?) {
//         self.owner = owner
//     }
// }
//
// /// Holds an unowned reference to an Owner. The owner must outlive this object.
// public class UnownedReferenceHolder {
//     public unowned var owner: Owner
//
//     public init(owner: Owner) {
//         self.owner = owner
//     }
// }
//
// /// Creates a paired Owner and WeakReferenceHolder for testing weak reference semantics.
// public func createWeakPair() -> (Owner, WeakReferenceHolder) {
//     let owner = Owner(name: "SharedOwner")
//     let holder = WeakReferenceHolder(owner: owner)
//     return (owner, holder)
// }

// MARK: - Class with Multiple Constructors (@_cdecl wrapper gap test)

/// Tests that ALL class constructors get @_cdecl wrappers, not just those with
/// non-trivial parameters. Previously, parameterless constructors and constructors
/// with only primitive params were left as CallConvSwift, which crashed Mono JIT.
public class MultiInitClass {
    public var label: String
    public var value: Int32
    public var enabled: Bool

    /// Parameterless init — previously crashed Mono (Keychain(), MD5() pattern).
    public init() {
        self.label = "default"
        self.value = 0
        self.enabled = false
    }

    /// Bool param init — MarshalAs + CallConvSwift crash (BooleanDisposable(bool) pattern).
    public init(enabled: Bool) {
        self.label = enabled ? "enabled" : "disabled"
        self.value = enabled ? 1 : 0
        self.enabled = enabled
    }

    /// String + Int32 param init — already had @_cdecl (string param triggers wrapper).
    public init(label: String, value: Int32) {
        self.label = label
        self.value = value
        self.enabled = true
    }

    public func describe() -> String {
        return "\(label):\(value):\(enabled)"
    }
}

/// Free function factory for MultiInitClass.
public func createMultiInitDefault() -> MultiInitClass {
    return MultiInitClass()
}

// MARK: - Final Class with Read/Write Properties (@_cdecl wrapper gap test)

/// Tests that final class instance property accessors get @_cdecl wrappers.
/// Previously, final class properties used CallConvSwift + SwiftSelf which crashed
/// Mono JIT (ImagePrefetcher.Priority, ImageTask.Priority pattern).
public final class FinalPropertyHolder {
    public var intValue: Int32
    public var floatValue: Double
    public var stringValue: String
    public var boolValue: Bool

    public init(intValue: Int32, floatValue: Double, stringValue: String, boolValue: Bool) {
        self.intValue = intValue
        self.floatValue = floatValue
        self.stringValue = stringValue
        self.boolValue = boolValue
    }

    /// Computed property — getter uses @_cdecl wrapper.
    public var summary: String {
        return "\(intValue),\(floatValue),\(stringValue),\(boolValue)"
    }
}
