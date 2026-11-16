// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - NSObject subclasses whose Swift members meet NSObject's C# surface

private nonisolated(unsafe) var inheritedKeyChangeCount: Int32 = 0
private nonisolated(unsafe) var inheritedLastChangedKey: String = ""

/// How many times `InheritedHookObject.didChangeValue(forKey:)` ran in Swift.
public func getInheritedKeyChangeCount() -> Int32 { inheritedKeyChangeCount }

/// The key the last `InheritedHookObject.didChangeValue(forKey:)` call received.
public func getInheritedLastChangedKey() -> String { inheritedLastChangedKey }

/// Overrides NSObject's `description`, `debugDescription`, `hash` and `isEqual(_:)`. NSObject's C#
/// projection declares the same members as virtual, so the binding has to override them: a member
/// that hides them leaves every NSObject-typed caller (`ToString()`, collections, string
/// interpolation) on NSObject's own implementation.
public class InheritedDescriptionObject: NSObject {
    public let name: String

    public init(name: String) {
        self.name = name
        super.init()
    }

    public override var description: String { "described:\(name)" }
    public override var debugDescription: String { "debug-described:\(name)" }
    public override var hash: Int { name.count }

    public override func isEqual(_ object: Any?) -> Bool {
        guard let other = object as? InheritedDescriptionObject else { return false }
        return other.name == name
    }
}

/// Overrides `description` a second time, one level down.
public class InheritedDescriptionChild: InheritedDescriptionObject {
    public override var description: String { "child:\(name)" }
}

/// Overrides an inherited NSObject method and declares members whose names NSObject's C# surface
/// already uses for something else (`Handle`, `MarkDirty`).
public class InheritedHookObject: NSObject {
    /// Shares its name with NSObject's native handle; the binding renames it.
    public var handle: Int32

    public init(handle: Int32) {
        self.handle = handle
        super.init()
    }

    public override func didChangeValue(forKey key: String) {
        inheritedKeyChangeCount += 1
        inheritedLastChangedKey = key
        super.didChangeValue(forKey: key)
    }

    /// Unrelated to NSObject's `MarkDirty()`, which the binding cannot override.
    public func markDirty() -> Int32 {
        handle += 1
        return handle
    }
}

/// Overrides the renamed `handle` one level down: the override must keep the base's C# name, or it
/// targets NSObject's non-virtual `Handle` instead of the base property.
public class InheritedHookChild: InheritedHookObject {
    public override init(handle: Int32) {
        super.init(handle: handle)
    }

    public override var handle: Int32 {
        get { super.handle + 100 }
        set { super.handle = newValue }
    }
}

/// A protocol requirement whose C# name an NSObject subclass cannot use for its own property.
public protocol SuperHandleCarrying {
    var superHandle: Int32 { get }
}

/// Satisfies `SuperHandleCarrying.superHandle` with a property the binding renames off NSObject's
/// `SuperHandle`; the interface member has to be implemented explicitly.
public class InheritedHandleCarrier: NSObject, SuperHandleCarrying {
    public let superHandle: Int32

    public init(superHandle: Int32) {
        self.superHandle = superHandle
        super.init()
    }
}
