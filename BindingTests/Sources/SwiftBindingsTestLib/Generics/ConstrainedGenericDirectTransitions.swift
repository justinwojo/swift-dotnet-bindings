// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Members of a constrained generic struct that reach Swift through direct transitions
//
// A struct generic over a protocol-constrained parameter and an Error has no @_cdecl wrapper the
// generator can specialize, so each member below is a direct Swift-convention import that only a
// native bridge carries on the engines that mishandle the self register. Each one is a shape the
// direct import once declared wrongly, which kept its bridge plan refused:
//
// - `echoed()` returns the resilient generic struct itself, and `keyedResult()` and
//   `countResult(succeeding:)` return a `Result` whose payload is address-only (a generic parameter, or
//   a resilient error). Swift returns all three through the indirect-result register; the import
//   returned a pointer in the first result register instead.
// - `echoNarrow`, `echoByte` and `echoFlag` take and return Optionals of sub-word primitives, which
//   Swift lowers to an i32, i16 and i8; the import declared a whole word for each.
//
// The callee recovers both generic arguments and the KeyedValue witness table from self's metadata,
// so the trailing metadata and witness-table arguments the import passes are never read.

public protocol KeyedValue {
    var key: Int { get }
}

public struct KeyedToken: KeyedValue {
    public let key: Int

    public init(key: Int) { self.key = key }
}

public struct DirectTransitionFailure: Error {
    public let code: Int

    public init(code: Int) { self.code = code }
}

public struct DirectTransitionBox<Element: KeyedValue, Failure: Error> {
    var element: Element

    public init(element: Element) { self.element = element }

    /// The element's key, read through the protocol.
    public var elementKey: Int { element.key }

    /// A copy of the box, returned through the indirect-result register.
    public func echoed() -> DirectTransitionBox<Element, Failure> { self }

    /// The element as a `Result` over the box's own generic parameters.
    public func keyedResult() -> Result<Element, Failure> { .success(element) }

    /// The element's key on success, or a failure carrying the negated key.
    public func countResult(succeeding: Bool) -> Result<Int, DirectTransitionFailure> {
        succeeding ? .success(element.key) : .failure(DirectTransitionFailure(code: -element.key))
    }

    /// The value plus the element's key, or nil when the value is nil.
    public func echoNarrow(_ value: UInt16?) -> UInt16? {
        value.map { $0 &+ UInt16(truncatingIfNeeded: element.key) }
    }

    /// The value plus one, or nil when the value is nil.
    public func echoByte(_ value: UInt8?) -> UInt8? { value.map { $0 &+ 1 } }

    /// The negated flag, or nil when the flag is nil.
    public func echoFlag(_ value: Bool?) -> Bool? { value.map { !$0 } }
}

/// The key of the element inside a box, read in Swift: checks what a box returned to C# holds.
public func directTransitionBoxKey(_ box: DirectTransitionBox<KeyedToken, DirectTransitionFailure>) -> Int {
    box.elementKey
}
