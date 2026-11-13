// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Returned closures whose arguments or results need marshalling beyond a plain value
//
// Each closure captures state, so a call that loses the closure context (the context register)
// computes the wrong answer or faults instead of passing by luck. The values are chosen so a
// result read from the wrong place cannot coincide with the right one.

/// An opaque (non-class-bound) protocol: its existential is address-only in every abstraction.
public protocol ReturnedClosureShape {
    func area() -> Int32
}

/// A class-bound protocol: its existential is two words (reference + witness table), loadable,
/// and passed by value at its natural abstraction.
public protocol ReturnedClosureObjectShape: AnyObject {
    func area() -> Int32
}

public struct ReturnedClosureSquare: ReturnedClosureShape {
    public var side: Int32
    public init(side: Int32) { self.side = side }
    public func area() -> Int32 { side * side }
}

public final class ReturnedClosureObjectSquare: ReturnedClosureObjectShape {
    public var side: Int32
    public init(side: Int32) { self.side = side }
    public func area() -> Int32 { side * side }
}

/// A non-frozen struct argument, the shape a returned factory closure commonly takes.
public struct ReturnedClosureConfig {
    public var scale: Int32
    public var label: String
    public init(scale: Int32, label: String) {
        self.scale = scale
        self.label = label
    }
}

public struct ReturnedClosureError: Error {
    public var code: Int32
    public init(code: Int32) { self.code = code }
}

/// Vends closures whose argument or result is an existential, a tuple, or an error.
public final class ReturnedClosureVendor {
    private let offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    /// Existential argument, scalar result.
    public func makeShapeMeasurer() -> (any ReturnedClosureShape) -> Int32 {
        let offset = self.offset
        return { shape in shape.area() + offset }
    }

    /// Scalar argument, existential result.
    public func makeShapeBuilder() -> (Int32) -> any ReturnedClosureShape {
        let offset = self.offset
        return { side in ReturnedClosureSquare(side: side + offset) }
    }

    /// Class-bound existential argument.
    public func makeObjectShapeMeasurer() -> (any ReturnedClosureObjectShape) -> Int32 {
        let offset = self.offset
        return { shape in shape.area() + offset }
    }

    /// Class-bound existential result.
    public func makeObjectShapeBuilder() -> (Int32) -> any ReturnedClosureObjectShape {
        let offset = self.offset
        return { side in ReturnedClosureObjectSquare(side: side + offset) }
    }

    /// Non-frozen struct argument, existential result.
    public func makeConfiguredShapeBuilder() -> (ReturnedClosureConfig) -> any ReturnedClosureShape {
        let offset = self.offset
        return { config in ReturnedClosureSquare(side: config.scale + offset) }
    }

    /// Tuple result.
    public func makePairMaker() -> (Int32) -> (Int32, Int32) {
        let offset = self.offset
        return { value in (value + offset, value * offset) }
    }

    /// Throwing closure with an existential argument.
    public func makeThrowingShapeMeasurer() -> (any ReturnedClosureShape) throws -> Int32 {
        let offset = self.offset
        return { shape in
            let area = shape.area()
            if area < 0 { throw ReturnedClosureError(code: area) }
            return area + offset
        }
    }

    /// Error existential result.
    public func makeErrorBuilder() -> (Int32) -> any Error {
        let offset = self.offset
        return { code in ReturnedClosureError(code: code + offset) }
    }

    /// Optional closure property with an existential argument.
    public var optionalShapeMeasurer: ((any ReturnedClosureShape) -> Int32)? {
        let offset = self.offset
        return { shape in shape.area() + offset }
    }

    /// Optional closure property with a tuple result.
    public var optionalPairMaker: ((Int32) -> (Int32, Int32))? {
        let offset = self.offset
        return { value in (value - offset, value + offset) }
    }

    /// A C function pointer result. The wrapper stores it as an ordinary Swift closure, so C#
    /// calls it through the same invoke thunk as any other returned closure.
    public func makeCTripler() -> @convention(c) (Int32) -> Int32 {
        return { value in value * 3 }
    }

    // Refused shapes: each compiles to a call C# cannot make correctly, so the generator must
    // leave the member out rather than bind it.

    /// A subscript getter hands back its closure through the Swift calling convention and has
    /// no invoke thunk.
    public subscript(scale scale: Int32) -> (Int32) -> Int32 {
        let offset = self.offset
        return { value in value * scale + offset }
    }

    /// An async closure has no synchronous call C# can make.
    public func makeAsyncCounter() -> () async -> Int32 {
        let offset = self.offset
        return { offset }
    }

    /// A closure taking a closure: the invoke thunk has no marshalling for the inner closure.
    public func makeApplier() -> (@escaping (Int32) -> Int32) -> Int32 {
        let offset = self.offset
        return { body in body(offset) }
    }

    /// A closure taking a C function pointer.
    public func makeCFunctionApplier() -> (@convention(c) (Int32) -> Int32) -> Int32 {
        let offset = self.offset
        return { body in body(offset) }
    }

    /// A settable closure property with a tuple result. Its setter would hand Swift a C# callback
    /// that returns the tuple by value, which no callback signature can carry.
    public var storedPairMaker: ((Int32) -> (Int32, Int32))?

    /// Reads back the area of a shape this module built, to check a returned existential.
    public static func areaOf(_ shape: any ReturnedClosureShape) -> Int32 {
        shape.area()
    }

    /// Reads back the area of a class-bound shape passed as an ordinary method argument.
    public static func objectAreaOf(_ shape: any ReturnedClosureObjectShape) -> Int32 {
        shape.area()
    }

    /// Reads back the code of an error a returned closure built.
    public static func codeOf(_ error: any Error) -> Int32 {
        (error as? ReturnedClosureError)?.code ?? -1
    }
}
