// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - AnyObject across the @_cdecl wrapper
//
// `AnyObject` is one class reference with no witness table, and C# sees it as `object`. Like every
// existential it crosses a @_cdecl wrapper through a pointer to a cell holding the value: Swift
// copies an argument out with `load(as:)` and writes a result into the caller's buffer with
// `initializeMemory(as:)`. The cell is one word, the reference itself.
//
// The wrapper used to read an argument's pointer as the object while C# passed the address of a
// five-word container, and returned the object by value while C# read five words out of a result
// buffer nothing wrote. Each member below lets the C# side check what Swift saw: the object's
// identity (`===`), its identifier, and the live count, which a leaked or over-released reference
// moves.

/// A class instance whose lifetime is observable from C#. It is Sendable so it can also cross as
/// `any AnyObject & Sendable`; the tests touch the live count from one thread at a time.
public final class AnyObjectLaneToken: @unchecked Sendable {
    private static var live: Int32 = 0

    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
        Self.live += 1
    }

    deinit {
        Self.live -= 1
    }

    public static var liveCount: Int32 { live }
}

/// The identifier of a token, -1 for any other object.
public func anyObjectLaneIdentifier(_ object: AnyObject) -> Int32 {
    (object as? AnyObjectLaneToken)?.identifier ?? -1
}

/// Whether two arguments are the same object.
public func anyObjectLaneSame(_ first: AnyObject, _ second: AnyObject) -> Bool {
    first === second
}

/// The dynamic type Swift sees, so an argument that arrived as the wrong object shows up.
public func anyObjectLaneTypeName(_ object: AnyObject) -> String {
    String(describing: type(of: object))
}

/// A new token, returned as `AnyObject`.
public func anyObjectLaneMake(_ identifier: Int32) -> AnyObject {
    AnyObjectLaneToken(identifier: identifier)
}

/// The argument, returned unchanged.
public func anyObjectLaneEcho(_ object: AnyObject) -> AnyObject {
    object
}

// A marker protocol adds no witness table, so `any AnyObject & Sendable` is still a one-word cell.

public func anyObjectLaneSendableIdentifier(_ object: any AnyObject & Sendable) -> Int32 {
    (object as? AnyObjectLaneToken)?.identifier ?? -1
}

public func anyObjectLaneSendableEcho(_ object: any AnyObject & Sendable) -> any AnyObject & Sendable {
    object
}

/// A reference type holding an object in every member position the wrapper reaches.
public final class AnyObjectLaneSink {
    public var stored: AnyObject

    public static var shared: AnyObject = AnyObjectLaneToken(identifier: 900)

    public init(stored: AnyObject) {
        self.stored = stored
    }

    public func holds(_ object: AnyObject) -> Bool {
        stored === object
    }

    /// Stores the argument and returns the object it replaces.
    public func exchange(_ object: AnyObject) -> AnyObject {
        let previous = stored
        stored = object
        return previous
    }

    /// The stored object, through a subscript getter and setter.
    public subscript(slot: Int) -> AnyObject {
        get { stored }
        set { stored = newValue }
    }

    public func storedOrThrow(_ fail: Bool) throws -> AnyObject {
        if fail { throw AnyObjectLaneFailure() }
        return stored
    }

    public func storedAsync() async -> AnyObject {
        stored
    }

    public func holdsAsync(_ object: AnyObject) async -> Bool {
        stored === object
    }
}

public struct AnyObjectLaneFailure: Error {}

/// A value type with an `AnyObject` stored property, reached through the struct's own wrappers.
public struct AnyObjectLaneBox {
    public var item: AnyObject

    public init(item: AnyObject) {
        self.item = item
    }

    public func holds(_ object: AnyObject) -> Bool {
        item === object
    }
}

// MARK: Reverse dispatch

/// A protocol C# implements, so Swift hands `AnyObject` to C# and takes one back. Requirement names
/// are unique in the module: protocols share one reverse-dispatch class.
public protocol AnyObjectLaneExchanger {
    var laneCurrent: AnyObject { get set }
    func laneSwap(_ incoming: AnyObject) -> AnyObject
}

/// Drives a conformer through every requirement and checks identity at each step: the swap must
/// return the object held before it, the property must then read `incoming`, and a write must land.
/// Returns the first step that failed, or "ok".
public func anyObjectLaneDrive(_ exchanger: any AnyObjectLaneExchanger, _ incoming: AnyObject) -> String {
    var conformer = exchanger
    let before = conformer.laneCurrent
    let returned = conformer.laneSwap(incoming)
    guard returned === before else { return "swap returned \(type(of: returned)), not the previous object" }
    guard conformer.laneCurrent === incoming else { return "current is not the swapped-in object" }
    conformer.laneCurrent = before
    guard conformer.laneCurrent === before else { return "the write did not land" }
    return "ok"
}
