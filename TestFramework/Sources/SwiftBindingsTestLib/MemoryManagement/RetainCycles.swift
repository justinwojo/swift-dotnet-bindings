// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Retain Cycle Patterns (Known Unsupported)

// These types demonstrate circular reference and cycle-breaking patterns.
// They are known-unsupported features documenting patterns for future implementation.
// The binding generator does not yet handle weak/unowned cycle-breaking in
// the context of parent-child or delegate relationships.

// MARK: - Strong Circular Reference (intentional leak)

/// Two classes that hold strong references to each other, creating a retain cycle.
/// This is the classic memory leak pattern that weak/unowned references solve.
/// The binding generator must understand this pattern to warn or handle it.
public class StrongNodeA {
    public var name: String
    public var partner: StrongNodeB?

    public init(name: String) {
        self.name = name
    }

    public func describe() -> String {
        return "A(\(name))->B(\(partner?.name ?? "nil"))"
    }
}

/// Counterpart to StrongNodeA — holds a strong back-reference creating a cycle.
public class StrongNodeB {
    public var name: String
    public var partner: StrongNodeA?

    public init(name: String) {
        self.name = name
    }

    public func describe() -> String {
        return "B(\(name))->A(\(partner?.name ?? "nil"))"
    }
}

// MARK: - Tree Node with Weak Parent (weak cycle breaking)

/// Parent-child tree structure where children hold strong refs to parent
/// would create retain cycles. The `weak var parent` breaks the cycle.
public class TreeNode {
    public var name: String
    public var children: [TreeNode]
    public weak var parent: TreeNode?

    public init(name: String) {
        self.name = name
        self.children = []
    }

    public func addChild(_ child: TreeNode) {
        children.append(child)
        child.parent = self
    }

    public func depth() -> Int32 {
        var d: Int32 = 0
        var current = parent
        while let p = current {
            d += 1
            current = p.parent
        }
        return d
    }
}

// MARK: - Owner/Resource with Unowned (unowned cycle breaking)

/// Resource owner that holds strong references to its resources.
public class ResourceOwner {
    public var label: String
    public var resources: [OwnedResource]

    public init(label: String) {
        self.label = label
        self.resources = []
    }

    public func addResource(_ name: String) -> OwnedResource {
        let resource = OwnedResource(name: name, owner: self)
        resources.append(resource)
        return resource
    }
}

/// A resource that references its owner via `unowned` to avoid a retain cycle.
/// The resource's lifetime is guaranteed to be shorter than the owner's.
public class OwnedResource {
    public var name: String
    public unowned var owner: ResourceOwner

    public init(name: String, owner: ResourceOwner) {
        self.name = name
        self.owner = owner
    }

    public func ownerLabel() -> String {
        return owner.label
    }
}

// MARK: - Delegate Pattern with Weak Reference

/// Protocol for delegate callbacks.
public protocol Delegate: AnyObject {
    func didComplete(result: String)
}

/// Holder that calls its delegate. Uses `weak var` to avoid retaining the delegate.
public class DelegateHolder {
    public weak var delegate: Delegate?
    public var lastResult: String

    public init() {
        self.lastResult = ""
    }

    public func performWork() {
        let result = "completed"
        lastResult = result
        delegate?.didComplete(result: result)
    }
}

/// Concrete delegate implementation.
public class DelegateImpl: Delegate {
    public var receivedResult: String

    public init() {
        self.receivedResult = ""
    }

    public func didComplete(result: String) {
        receivedResult = result
    }
}

// MARK: - Delegate Fixture

/// Bundles a DelegateHolder with its DelegateImpl so the caller keeps both alive.
/// Without this, the weak delegate reference would be nil after the factory returns.
public class DelegateFixture {
    public var holder: DelegateHolder
    public var impl: DelegateImpl

    public init() {
        self.holder = DelegateHolder()
        self.impl = DelegateImpl()
        self.holder.delegate = self.impl
    }
}

// MARK: - Factory Functions

/// Creates a strong A↔B reference cycle (intentional leak for testing).
public func createStrongCycle() -> StrongNodeA {
    let a = StrongNodeA(name: "alpha")
    let b = StrongNodeB(name: "beta")
    a.partner = b
    b.partner = a
    return a
}

/// Creates a tree with a parent-child cycle (broken by weak parent reference).
public func createTreeCycle() -> TreeNode {
    let root = TreeNode(name: "root")
    let child = TreeNode(name: "child")
    root.addChild(child)
    return root
}

/// Creates an owner-resource pair (cycle broken by unowned reference).
public func createOwnerResourcePair() -> ResourceOwner {
    let owner = ResourceOwner(label: "primary")
    _ = owner.addResource("resource1")
    return owner
}

/// Creates a delegate fixture with holder and impl both kept alive.
/// The holder's weak delegate reference remains valid because the fixture
/// retains the impl.
public func createDelegatePattern() -> DelegateFixture {
    let fixture = DelegateFixture()
    fixture.holder.performWork()
    return fixture
}
