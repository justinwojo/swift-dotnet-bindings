// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

public protocol PathLeaf { associatedtype Element }
public protocol PathRoot { associatedtype Child: PathLeaf; associatedtype Element }
public struct PathIntChild: PathLeaf { public typealias Element = Int32 }
public struct PathStringChild: PathLeaf { public typealias Element = String }

public struct PathValidRoot: PathRoot {
    public typealias Child = PathIntChild
    public typealias Element = String
    public init() {}
}

public struct PathInverseRoot: PathRoot {
    public typealias Child = PathStringChild
    public typealias Element = Int32
    public init() {}
}

public struct AssociatedPathHost {
    public init() {}
    public func readChild<T: PathRoot>(_ value: T) -> Int32 where T.Child.Element == Int32 { 42 }
    public func readRoot<T: PathRoot>(_ value: T) -> Int32 where T.Element == Int32 { 17 }
}
