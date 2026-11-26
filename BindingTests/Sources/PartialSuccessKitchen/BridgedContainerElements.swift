// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Collections Swift hands across as the Objective-C collection they bridge to (a key or element
// that is itself Objective-C-bridgeable, here `URL`), holding values Swift boxes opaquely when it
// bridges them. C# can neither build nor read such a box, so each member below is refused at
// generation time with a reason of its own, never emitted against a native Swift collection.

public struct KitchenBridgedValue {
    public var x: Int32
    public init(x: Int32) { self.x = x }
}

public enum KitchenBridgedChoice { case first, second }

// A Swift struct as the value of a bridged dictionary, as a parameter and as a return.
public func kitchenBridgedStructs(_ d: [URL: KitchenBridgedValue]) -> Int32 { Int32(d.count) }
public func kitchenMakeBridgedStructs() -> [URL: KitchenBridgedValue] { [:] }

// The same struct one container deeper, inside an array the bridged dictionary holds.
public func kitchenBridgedStructArrays(_ d: [URL: [KitchenBridgedValue]]) -> Int32 { Int32(d.count) }

// A Swift enum and a UUID as bridged values, and a stored property of the struct shape.
public func kitchenBridgedChoices(_ d: [URL: KitchenBridgedChoice]) -> Int32 { Int32(d.count) }
public func kitchenBridgedIdentifiers(_ d: [URL: UUID]) -> Int32 { Int32(d.count) }

public final class KitchenBridgedHolder {
    public init() {}
    public var values: [URL: KitchenBridgedValue] = [:]
}

// A set of Swift objects one container deeper: Swift bridges it to an NSSet of the objects
// themselves, which C# builds only as a set of Objective-C objects.
public final class KitchenBridgedNode: Hashable {
    public init() {}
    public static func == (a: KitchenBridgedNode, b: KitchenBridgedNode) -> Bool { a === b }
    public func hash(into hasher: inout Hasher) { hasher.combine(ObjectIdentifier(self)) }
}
public func kitchenBridgedNodeSets(_ d: [URL: Set<KitchenBridgedNode>]) -> Int32 { Int32(d.count) }

// The same refusals for an enum case's associated value and for protocol requirements, which
// reach the generator through paths of their own. The plain case and requirement still bind.
public enum KitchenBridgedPayload {
    case values([URL: KitchenBridgedValue])
    case identifiers([URL: UUID])
    case none
}

public protocol KitchenBridgedIndexer {
    func makeIdentifiers() -> [URL: UUID]
    func takeValues(_ d: [URL: KitchenBridgedValue]) -> Int32
    func count() -> Int32
}
