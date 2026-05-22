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

// MARK: - Cross-Module Property Name + Type Collision Gate
//
// Locks in the union-aware property-type-count gate in
// ModuleHandler.EmitEveryProtocolConformances. Before the gate considered
// cross-module parents, the EveryProtocol property-emission-ownership map
// would key `(name, type)` separately for `crossModuleConflictedId: Int32`
// (from the cross-module parent below) and `crossModuleConflictedId: String`
// (from the unrelated local protocol). Both bodies would emit on
// EveryProtocol, and swiftc would reject with "invalid redeclaration of
// 'crossModuleConflictedId'", which the strip-retry loop would mask. The fix
// drops the conflicting local protocol so only the cross-module parent's
// body emits; the local protocol that inherits the parent (and adds no
// property of its own) keeps its conformance.

/// Local protocol that inherits the cross-module parent declaring
/// `var crossModuleConflictedId: Int32 { get }`. Adds no own properties — only
/// here to drag the parent into `crossModuleParents` so the gate is exercised.
public protocol LocalConflictingPropertyChild: CrossModuleConflictingPropertyParent {
    func crossModuleChildNotify(value: Int32)
}

/// Unrelated local protocol whose `crossModuleConflictedId` is `String`. Has no
/// inheritance relationship to the cross-module parent — the conflict crosses
/// the module boundary purely through property-name overlap. The gate drops
/// THIS protocol so the wrapper compiles.
public protocol LocalConflictingPropertyUnrelated: AnyObject {
    var crossModuleConflictedId: String { get }
}

// MARK: - Cross-Module Member-Kind (var-vs-func) Collision Gate
//
// Locks in the union-aware member-kind collision gate (bug 3's analog across
// module boundaries). The cross-module parent declares
// `var crossModuleLabel: Int32 { get }`; the unrelated local protocol below
// declares `func crossModuleLabel() -> Int32`. Both shapes on EveryProtocol
// would trigger Swift's "invalid redeclaration of 'crossModuleLabel()'"
// rejection. The gate detects the property-name appearing on either side of
// the union and drops the method-side protocol — here, the unrelated local.

/// Local protocol that inherits the cross-module parent declaring
/// `var crossModuleLabel: Int32 { get }`. Adds no own properties of that
/// name — only here to drag the parent into `crossModuleParents`.
public protocol LocalMemberKindChild: CrossModuleMemberKindPropertyParent {
    func crossModuleMemberKindChildNotify(value: Int32)
}

/// Unrelated local protocol whose `crossModuleLabel` is a zero-arg method,
/// colliding with the parent's `var crossModuleLabel`. Dropped by the gate.
public protocol LocalMemberKindUnrelated: AnyObject {
    func crossModuleLabel() -> Int32
}

// MARK: - Cross-Module Inverse Member-Kind + Same-Module-Hop Cascade
//
// Locks in BOTH:
// 1. The inverse direction of the member-kind gate (parent contributes the
//    function side; local contributes the property side) → the parent is
//    dropped from `crossModuleParents` and the cascade-drop step removes
//    any local that inherits it.
// 2. The same-module-hop walk in `TransitivelyInheritsCrossModuleParent` —
//    `LocalInverseMemberKindGrandchild` reaches the dropped parent only
//    via a same-module intermediate (`LocalInverseMemberKindChild`).
//    Without the same-module branch in the helper, the grandchild would
//    survive cascade-drop and emit a broken conformance.

/// Local protocol that inherits the cross-module inverse-member-kind parent.
/// Direct cascade-drop target (its inheritance chain reaches the dropped
/// parent in one hop).
public protocol LocalInverseMemberKindChild: CrossModuleInverseMemberKindParent {
    func crossModuleInverseChildNotify(value: Int32)
}

/// Local grandchild that reaches the dropped parent through a same-module
/// intermediate. Without same-module-hop resolution in the cascade helper,
/// this would erroneously survive.
public protocol LocalInverseMemberKindGrandchild: LocalInverseMemberKindChild {
    func crossModuleInverseGrandchildNotify(value: Int32)
}

/// Unrelated local protocol whose `crossModuleInverseLabel` is a `var`,
/// colliding with the parent's `func crossModuleInverseLabel()`. The gate
/// keeps this local (property side) and drops the parent (function side).
public protocol LocalInverseMemberKindUnrelated: AnyObject {
    var crossModuleInverseLabel: Int32 { get }
}
