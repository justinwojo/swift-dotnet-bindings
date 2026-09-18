// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A Swift conformer of a class-bound protocol passed back to Swift
//
// A value of `any P` where `P: AnyObject` is two words: the object reference and the witness
// table. The binding carries every existential in one widened container and Swift reads a
// class-bound one from its first two words, so the witness table has to sit in the second word
// whichever side made the container. A C# implementation (wrapped in a proxy) already put it
// there; a Swift class instance projected into C# and handed back is the case this covers.

/// A class-bound protocol with a Swift class conformer.
public protocol PassBackCounter: AnyObject {
    func passBackValue() -> Int32
    func passBackBump(_ amount: Int32)
}

/// The Swift-side conformer the C# caller constructs, or receives from Swift, and passes back.
public final class SwiftPassBackCounter: PassBackCounter {
    public private(set) var total: Int32

    public init(start: Int32) { total = start }

    public func passBackValue() -> Int32 { total }
    public func passBackBump(_ amount: Int32) { total += amount }
}

/// Reads a requirement through the existential.
public func passBackRead(_ counter: any PassBackCounter) -> Int32 {
    counter.passBackValue()
}

/// Optional existential parameter: `nil` reads as -1.
public func passBackReadOptional(_ counter: (any PassBackCounter)?) -> Int32 {
    counter?.passBackValue() ?? -1
}

/// Whether the existential refers to the same object as the concrete reference.
public func passBackIsSame(_ counter: any PassBackCounter, _ concrete: SwiftPassBackCounter) -> Bool {
    counter === concrete
}

/// Returns a Swift conformer typed as the existential, for the C# caller to pass back.
public func makePassBackCounter(_ start: Int32) -> any PassBackCounter {
    SwiftPassBackCounter(start: start)
}

/// Stores the existential and mutates the conformer through a method parameter.
public final class PassBackHolder {
    public var held: (any PassBackCounter)?

    public init() {}

    public func bump(_ counter: any PassBackCounter, by amount: Int32) -> Int32 {
        counter.passBackBump(amount)
        return counter.passBackValue()
    }

    public func readHeld() -> Int32 { held?.passBackValue() ?? -1 }
}
