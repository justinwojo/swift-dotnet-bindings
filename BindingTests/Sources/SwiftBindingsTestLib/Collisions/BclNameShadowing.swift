// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Public Swift types whose projected names shadow BCL names used by emitted interop
//
// Generated C# lands inside `namespace <SwiftModule>`, and the emitted P/Invoke boilerplate
// references BCL names (`System.Type`, the interop attributes, the calling-convention types).
// A public Swift type that projects onto one of those simple names sits in the SAME namespace
// as the emitted references, so an unqualified reference binds to the Swift type instead of
// the BCL one. The `Type` case is the sharp one: `new Type[] { typeof(CallConvCdecl) }` then
// reads as an array of the Swift enum, which is a hard error AND makes the LibraryImport
// source generator bail — taking every P/Invoke in the module down with it, not just this file.
//
// Every emitted BCL reference must therefore be `global::`-qualified. These declarations exist
// purely to occupy the colliding names; the members give the module real P/Invoke surface so
// the emitted attributes are actually exercised alongside them.
//
// Deliberately NOT covered here: names that Swift itself resolves (e.g. `Task`), which would
// shadow the stdlib for this module's own sources rather than testing the emitter.

public enum Type {
    case scalar
    case composite
}

public struct LibraryImport {
    public var slot: Int32
    public init(slot: Int32) { self.slot = slot }
}

public struct UnmanagedCallConv {
    public var flag: Bool
    public init(flag: Bool) { self.flag = flag }
}

public enum StringMarshalling {
    case utf8
    case utf16
}

/// A module error type named like the BCL base exception. Emitted guards catch the BCL type,
/// so an unqualified `catch (Exception)` would bind to this enum and stop the module compiling.
public enum Exception: Error {
    case rejected(reason: String)
}

/// A generic enum and a generic class: their emitted eager-initialization helpers are guards that
/// catch the BCL exception type.
public enum ShadowChoice<Value> {
    case nothing
    case something(Value)

    public var isSomething: Bool {
        if case .something = self { return true }
        return false
    }
}

public class ShadowStack<Element> {
    private var items: [Element] = []

    public init() {}

    public func push(_ item: Element) { items.append(item) }

    public var count: Int { items.count }
}

public struct CallConvCdecl {
    public var arity: Int32
    public init(arity: Int32) { self.arity = arity }
}

/// Exercises the emitted P/Invoke surface from inside the module that declares the shadowing
/// names above. Each member returns a distinct value so a mis-bound call is observable.
public class BclShadowProbe {
    private var kind: Type

    public init() { self.kind = .scalar }

    /// A member REFERENCING the shadowing type, as opposed to the members below which only
    /// reference the module's other types. It does not project: the type reference names a
    /// type called `Type`, which reads the same as a metatype, so the member is recorded as an
    /// unsupported signature. It stays here so the shape is covered once the two are told apart.
    public func roundTripType(_ value: Type) -> Type {
        return value == .scalar ? .composite : .scalar
    }

    public func marshalling() -> StringMarshalling {
        return .utf8
    }

    public func describe(_ text: String) -> String {
        return "shadowed:\(text)"
    }

    /// Throws the module's own `Exception` error for a negative slot.
    public func checkedSlot(_ slot: Int32) throws -> Int32 {
        guard slot >= 0 else { throw Exception.rejected(reason: "negative slot \(slot)") }
        return slot &* 2
    }

    /// The payload case for a non-negative slot, the empty case otherwise.
    public func choose(_ slot: Int32) -> ShadowChoice<Int32> {
        return slot < 0 ? .nothing : .something(slot)
    }

    public func slotOf(_ value: LibraryImport) -> Int32 {
        return value.slot &+ 1
    }
}
