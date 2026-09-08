// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

public enum IdentityFirstError: Error {
    case unused
    case first
}

public enum IdentitySecondError: Error {
    case unused
    case second
}

/// Same argument labels must not select another overload's typed error contract.
public struct TypedErrorIdentityWorker {
    public init() {}

    public func run(_ value: Int32) throws(IdentityFirstError) -> Int32 {
        if value < 0 { throw .first }
        return value + 10
    }
    public func run(_ value: String) throws(IdentitySecondError) -> Int32 {
        if value.isEmpty { throw .second }
        return Int32(value.count) + 20
    }
    public func perform(_ value: Int32) async throws(IdentityFirstError) -> Int32 {
        if value < 0 { throw .first }
        return value + 30
    }
    public func perform(_ value: String) async throws(IdentitySecondError) -> Int32 {
        if value.isEmpty { throw .second }
        return Int32(value.count) + 40
    }
    // Untyped sibling participates in overload disambiguation but has no refinement.
    public func perform(_ value: Bool) async throws -> Int32 {
        if !value { throw IdentityFirstError.first }
        return 50
    }
}

public enum TypedErrorOwnerA {
    public struct Inner {
        public init() {}
        public func work() async throws(IdentityFirstError) -> Int32 { throw .first }
        public func check() throws(IdentityFirstError) -> Int32 { throw .first }
    }
}

public enum TypedErrorOwnerB {
    public struct Inner {
        public init() {}
        public func work() async throws(IdentitySecondError) -> Int32 { throw .second }
        public func check() throws(IdentitySecondError) -> Int32 { throw .second }
    }
}
