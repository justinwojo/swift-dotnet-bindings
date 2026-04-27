// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Class Inheritance (Bug #14)

/// Direct subclass of a class living in the dependency module. Reproduces the
/// RealityFoundation `ModelEntity : Entity` shape: the parent's ClassDecl is not in
/// the current module's `_typeDecls`, so the parser must fall back to the global
/// TypeDatabase. Without the fix this class emits as a flat C# sibling with no
/// `: SwiftBindingsTestLibDependency.DependencyBaseEntity` clause.
public class LocalChildEntity: DependencyBaseEntity {
    public var localValue: Int32

    public init(label: String, localValue: Int32) {
        self.localValue = localValue
        super.init(label: label)
    }

    public override func describe() -> String {
        return "Child[\(label):\(localValue)]"
    }

    public override func tag() -> Int32 {
        return localValue
    }

    public func localOnly() -> String {
        return "child-method"
    }
}

/// Three-level chain: LocalDeepEntity → DependencyMidEntity → DependencyBaseEntity.
/// Same-module derived class with a same-module-resolved parent that itself has a
/// cross-module parent. Exercises the SuperclassTypeName walk in
/// `GetRootBaseTypeNameWithGenerics`.
public class LocalGrandchildEntity: DependencyMidEntity {
    public var deepFlag: Bool

    public init(label: String, midTag: Int32, deepFlag: Bool) {
        self.deepFlag = deepFlag
        super.init(label: label, midTag: midTag)
    }

    public override func describe() -> String {
        return "Grand[\(label):\(midTag):\(deepFlag)]"
    }
}

/// Round-trip helper: returns the LocalChildEntity through the cross-module base type.
/// The Swift signature requires a static upcast, the generated C# binding therefore
/// types the return as `DependencyBaseEntity`. Round-tripping a `LocalChildEntity`
/// through this function and back to its concrete type proves the inheritance
/// declaration on the C# side is real (not just textually present).
public func upcastChildToBase(_ child: LocalChildEntity) -> DependencyBaseEntity {
    return child
}

/// Free function that consumes the cross-module base. Used to verify the C# call site
/// accepts a derived subclass argument without an explicit cast.
public func describeChild(_ child: LocalChildEntity) -> String {
    return describeBaseEntity(child)
}
