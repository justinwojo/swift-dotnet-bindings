// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @_cdecl wrapper return/name shapes that only appear on the reroute lanes
//
// Three shapes whose wrappers are emitted by code paths the rest of the corpus does not
// reach. Each one is a *Swift or C# compile* assertion first — an emitted wrapper that
// does not compile is dropped from the dylib rather than failing loudly, so the runtime
// tests below exist to prove the member is reachable at all, not merely that its value
// is right.

// MARK: 1. Generic-static-dispatch wrapper, throwing, pointer-shaped return
//
// A member on a GENERIC parent routes through the `_SBW_GSM_*` protocol-and-extension
// wrapper: a free `@_cdecl` recovers the parent metatype and dispatches statically.
// When that member also `throws` and returns a CLASS (or an Optional class), the
// wrapper's catch arm has to produce a value of the declared `@_cdecl` return type —
// `UnsafeMutableRawPointer` / `UnsafeMutableRawPointer?`. An integer sentinel does not
// convert to either, so the wrapper fails to compile and the member silently loses its
// wrapper. `Self`-free and `T`-free returns keep the value off the indirect-result path,
// which is what selects the direct-return arm being covered here.

/// Class return shape for the generic-static-dispatch throwing wrappers below.
public final class TokenReceipt {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

/// Thrown by the generic-static-dispatch fixtures so the catch arm is genuinely reachable.
public enum TokenError: Error, CustomStringConvertible {
    case refused(Int32)

    public var description: String {
        switch self {
        case .refused(let c): return "TokenError.refused(\(c))"
        }
    }
}

/// Generic parent — this is what puts its members on the generic-static-dispatch route.
/// `Slot` is never mentioned by the members below, so their returns stay direct rather
/// than crossing through the indirect-result buffer.
public struct TokenRequest<Slot> {
    private let _code: Int32

    public init(code: Int32) { self._code = code }

    /// Throwing + non-optional class return. The wrapper's catch arm must yield a
    /// non-optional `UnsafeMutableRawPointer`.
    public func issueReceipt() throws -> TokenReceipt {
        if _code < 0 { throw TokenError.refused(_code) }
        return TokenReceipt(code: _code)
    }

    /// Throwing + Optional class return. The wrapper's catch arm must yield `nil`, which
    /// is a different sentinel from the non-optional arm above.
    public func issueOptionalReceipt() throws -> TokenReceipt? {
        if _code < 0 { throw TokenError.refused(_code) }
        return _code == 0 ? nil : TokenReceipt(code: _code)
    }
}

// MARK: 2. Method-level-generic opening wrapper returning `Self`
//
// A member that declares its OWN generic parameter is rerouted onto a free `@_cdecl`
// that re-enters generic context through nested LOCAL generic functions (SE-0352
// implicit existential opening). A local function cannot spell `Self` — it has no
// enclosing type — so a `Self` return has to be written as the concrete parent type.
// `Self` returns are admitted for class parents only, which is why this is a class.

/// Non-final class so `Self` is a genuinely dynamic return rather than a spelling of
/// the declaring type, and so the method-level-generic opening route is the one taken.
public class ColumnSpec {
    private var _name: String
    private var _defaultDescription: String = ""

    public init(name: String) { self._name = name }

    public var name: String { _name }
    public var defaultDescription: String { _defaultDescription }

    /// Method-level generic + `Self` return — the shape whose opened body has to name the
    /// parent type instead of `Self`.
    public func defaulting<T: Describable>(to value: T) -> Self {
        _defaultDescription = value.describe()
        return self
    }
}

// MARK: 3. A parameter whose derived wrapper locals collide with the synthetic ones
//
// The C# wrapper body names its own locals by convention (`resultPtr` for the
// indirect-result buffer) and ALSO derives locals from each parameter by suffixing
// (`{param}Heap`, `{param}Ptr` for an existential parameter). A parameter spelled
// `result` makes the two families meet on one identifier, which is a C# redeclaration
// error rather than a mis-marshal — the binding does not compile at all. An existential
// parameter plus an existential return is the pairing that allocates both.

/// Existential parameter/return type for the collision fixture.
public protocol Measurable {
    func measure() -> Int32
}

/// Concrete conformer so the fixture is callable from C#.
public final class FixedMeasure: Measurable {
    private let _value: Int32
    public init(value: Int32) { self._value = value }
    public func measure() -> Int32 { _value }
}

/// Free function: an existential return (allocates the synthetic `resultPtr` buffer)
/// alongside an existential parameter literally named `result` (derives `resultPtr`
/// from the parameter). Both locals land in one C# method body.
public func chooseMeasurable(
    preferFirst: Bool,
    result: any Measurable,
    fallback: any Measurable
) -> any Measurable {
    return preferFirst ? result : fallback
}
