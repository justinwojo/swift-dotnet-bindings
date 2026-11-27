// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - An initializer that adds `T: AnyObject` to an unconstrained generic type
//
// The type takes any `T`, but one initializer accepts only a class. C# cannot put that constraint
// on a constructor, so the binding checks the type argument before calling Swift: a class builds
// the value, and anything else is refused in managed code instead of reaching a Swift cast that
// would stop the process.

public final class ClassConstrainedToken {
    public let id: Int
    public init(id: Int) { self.id = id }
}

public struct ClassConstrainedSlot<T> {
    public var value: T
    public var tag: Int
    public init(value: T) { self.value = value; self.tag = 0 }
    public init(object: T, tag: Int) where T: AnyObject { self.value = object; self.tag = tag }
}

// MARK: - Generic functions bound by `AnyObject` alone
//
// With no protocol in the bound, Swift passes and returns `U` as the object reference itself, not
// as the address of a value. A binding that passed an address would hand Swift a pointer into a
// managed buffer as if it were the object.

public func classConstrainedEcho<U: AnyObject>(_ value: U) -> U { value }

public func classConstrainedSame<U: AnyObject>(_ first: U, _ second: U) -> Bool { first === second }

/// The token's id when the argument is a token, else -1.
public func classConstrainedTokenId<U: AnyObject>(_ value: U) -> Int {
    (value as? ClassConstrainedToken)?.id ?? -1
}
