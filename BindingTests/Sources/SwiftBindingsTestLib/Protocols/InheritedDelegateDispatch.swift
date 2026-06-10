// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Inherited-protocol delegate dispatch
//
// Covers justinwojo/swift-dotnet-bindings#40: a child protocol that
// only inherits requirements from a parent protocol — `protocol ChildDelegate:
// ParentDelegate {}` with no new requirements of its own. The Swift API exposes a
// property typed as the child protocol; Swift's witness dispatch routes inherited
// calls through the parent protocol's vtable.
//
// Two bugs converge here, both required for delivery:
//
// 1. Generator must force the parent proxy class's cctor when the child proxy is
//    constructed. Without it the parent's Swift `_p_vtable` global stays nil and
//    Swift force-unwraps the nil function pointer on the first inherited call.
//
// 2. Generator must emit `IProtocolProxyImpl<TInterface>` on every proxy so the
//    parent's receiver can find a child-typed proxy via interface covariance and
//    reach the user's C# impl. Without it the receiver's typed lookup returns null
//    on sibling-proxy registrations and the callback is silently dropped.

public protocol InheritedParentDelegate: AnyObject {
    /// Inherited by the child below; the bug surfaces when a C# class implements
    /// only `IInheritedChildDelegate` and Swift invokes this through the parent's
    /// witness table.
    func parentDidNotify(value: Int32)
}

public protocol InheritedChildDelegate: InheritedParentDelegate {
    // Intentionally empty — the whole point. The child has no new requirements;
    // every callback Swift makes goes through the parent's protocol vtable.
}

// MARK: - 3-level inheritance chain (Grandchild → Child → Parent → AnyObject)
//
// Verifies the ancestor-proxy cctor cascade composes across N levels and that the
// receiver-side covariant `IProtocolProxyImpl<TInterface>` lookup resolves a
// grandchild-typed proxy when the parent's receiver looks up the user impl.

public protocol InheritedGrandchildDelegate: InheritedChildDelegate {
    // Intentionally empty — every callback flows through the parent's vtable, but
    // the cctor chain has to climb two ancestor levels (grandchild → child → parent)
    // for the parent's _p_vtable to be non-nil at dispatch time.
}

public class InheritedDelegate3LevelSource {
    public weak var grandchildDelegate: InheritedGrandchildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches through the **grandchild**-typed `weak var`. Inherited
    /// `parentDidNotify` must reach the C# impl via grandchild→child→parent
    /// witness-table forwarding. Pre-fix this crashes at the parent vtable.
    public func fireViaGrandchild(value: Int32) {
        if let d = grandchildDelegate {
            lastSlotFired = 1
            d.parentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

// MARK: - Non-empty child (child has its own member + inherits parent's)
//
// The reduction above used an empty child, but real-world inherited delegates
// typically carry their own requirements too. Verifies inherited dispatch still
// works when both the parent's and the child's witness tables have to be
// populated and routed.

public protocol InheritedNonEmptyChildDelegate: InheritedParentDelegate {
    /// Child's own requirement, on top of the inherited parentDidNotify.
    func childDidNotify(value: Int32)
}

public class InheritedNonEmptyChildSource {
    public weak var childDelegate: InheritedNonEmptyChildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches the **inherited** parent requirement through the child-typed slot.
    public func fireParentViaChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.parentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }

    /// Dispatches the **child's own** requirement through the child-typed slot.
    public func fireChildOwnMethod(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 2
            d.childDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

public class InheritedDelegateSource {
    public weak var childDelegate: InheritedChildDelegate?
    public weak var parentDelegate: InheritedParentDelegate?
    public var strongChildDelegate: InheritedChildDelegate?
    /// Records whether the last callback was actually delivered.
    /// 0 = not delivered (proxy lookup failed or vtable nil)
    /// 1 = parent-via-child slot fired
    /// 2 = direct-parent slot fired
    /// 3 = strong-child slot fired
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches through the **child**-typed `weak var`. The Swift compiler emits
    /// the call against `InheritedChildDelegate`'s witness table, which forwards
    /// `parentDidNotify` to the parent's vtable entry. Pre-fix the parent vtable is
    /// nil → SIGTRAP on force-unwrap.
    public func fireViaChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.parentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }

    /// Dispatches through a parent-typed `weak var` for control. Even pre-fix this
    /// path works (the parent's cctor fires from its own proxy construction).
    /// Used to prove the child-typed path is what regresses, not delegate dispatch
    /// generally.
    public func fireViaParent(value: Int32) {
        if let d = parentDelegate {
            lastSlotFired = 2
            d.parentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }

    /// Strong-storage variant so a C# test can drop its local reference and still
    /// receive the callback (mirrors the AutoWrappedDelegate test's strong slot).
    public func fireViaStrongChild(value: Int32) {
        if let d = strongChildDelegate {
            lastSlotFired = 3
            d.parentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

// MARK: - Cross-Module Inherited Delegate (parent in SwiftBindingsTestLibDependency)
//
// Repros the third remaining shape of the inherited-delegate bug — the
// cross-module variant. The parent protocol lives in a different module
// (SwiftBindingsTestLibDependency.CrossModuleParentDelegate); the child is
// declared here and inherits across the module boundary. The emitter must:
//
//   1. Emit a companion `extension EveryProtocol: CrossModuleParentDelegate`
//      on the main module's EveryProtocol that supplies the inherited
//      witness body — otherwise wrapper compile fails with
//      "type 'EveryProtocol' does not conform to 'CrossModuleParentDelegate'".
//   2. Have the C# IChild interface extend the cross-module IParent, so
//      `IProtocolProxyImpl<IChild>` resolves covariantly to
//      `IProtocolProxyImpl<IParent>` for the parent receiver's lookup.
//   3. Force the cross-module parent proxy's cctor from the child proxy's
//      InitializeVtable, so the parent's `_p_vtable` in the dependency
//      module is populated before Swift force-unwraps it.

public protocol CrossModuleInheritedChildDelegate: CrossModuleParentDelegate {
    // Intentionally empty — every callback flows through the parent's
    // cross-module witness table, exactly as an ad-network SDK's `SDKInitDelegate:
    // BaseSDKInitDelegate` inherited-delegate shape.
}

public class CrossModuleInheritedDelegateSource {
    public weak var childDelegate: CrossModuleInheritedChildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches through the **child**-typed `weak var`. Inherited
    /// `crossModuleDidNotify` must reach the C# impl via child(this module)
    /// → parent(dependency module) witness-table forwarding. Pre-fix this
    /// crashes at the parent vtable (or fails to wrapper-compile if the
    /// companion extension is missing).
    public func fireViaCrossModuleChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.crossModuleDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

// MARK: - Transitive cross-module ancestor (H1 fixture)
//
// Local child inherits `CrossModuleTransitiveParentDelegate` (dep module),
// which itself inherits `CrossModuleTransitiveGrandparentDelegate` (dep
// module). Pre-H1 the cctor only forced the DIRECT cross-module parent's
// vtable population, so dispatching the grandparent method through the
// child-typed slot hit a nil `_p_vtable` on the grandparent and crashed.
// The H1 fix walks ancestors transitively via BFS, populating BOTH levels.

public protocol CrossModuleTransitiveChildDelegate: CrossModuleTransitiveParentDelegate {
    // Intentionally empty — every callback flows through one of the two
    // cross-module ancestor witness tables. The local child contributes
    // nothing new; both parent AND grandparent methods are inherited.
}

public class CrossModuleTransitiveDelegateSource {
    public weak var childDelegate: CrossModuleTransitiveChildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches the **direct cross-module parent's** requirement through
    /// the child-typed slot. Works pre-H1 because the parent gets populated
    /// via the existing direct-ancestor path — control case.
    public func fireParentViaTransitiveChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.crossModuleParentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }

    /// Dispatches the **transitive grandparent's** requirement through the
    /// child-typed slot. Crashes pre-H1 (grandparent `_p_vtable` is nil),
    /// passes post-H1 (BFS populated grandparent during cctor).
    public func fireGrandparentViaTransitiveChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 2
            d.crossModuleGrandparentDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

// MARK: - Closure-property cross-module parent (H2 fixture)
//
// `CrossModuleClosurePropertyParentDelegate` (dep module) has BOTH a
// non-dispatchable closure property AND a dispatchable method. The local
// child below inherits across the module boundary. Pre-H2 the C# vtable
// struct emitted on the cross-module path included the closure-property
// slot while Swift's wrapper vtable skipped it — layouts diverged, and
// invoking the method through the child slot fired the wrong function
// pointer (or SIGSEGV'd). Post-H2 both sides apply the same
// `ProtocolVtableMembers` filter, so layouts match exactly.

public protocol CrossModuleClosurePropertyChildDelegate: CrossModuleClosurePropertyParentDelegate {
    // Intentionally empty — the test fires the inherited non-closure method
    // through this child slot. If H2 regresses, the method dispatch reads a
    // misaligned slot.
}

public class CrossModuleClosurePropertyDelegateSource {
    public weak var childDelegate: CrossModuleClosurePropertyChildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches the dispatchable parent method through the child-typed
    /// slot. With H2 in place, both the Swift wrapper vtable and the C#
    /// cross-module-parent vtable skip the closure property slot, so this
    /// method lands at the same offset on both sides.
    public func fireNonClosureViaChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.nonClosureDidNotify(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}

// MARK: - Cross-module parent with skipped-method-before-dispatchable-method
//
// Companion local child for `CrossModuleSkippedMethodParentDelegate` (dep
// module). Pre-fix the cctor's filter-before-increment ordering would assign
// the dispatchable parent method to slot 0, but both vtable structs reserve
// slot 0 for the filtered two-closure method `twoClosureSkip(first:second:)`
// and place `dispatchableAfterSkippedMethod` at slot 1. The wrapper would
// either fail to compile (field name mismatch with `_0` vs `_1` suffix) or,
// in the degenerate case where suffixes collide, silently wire the
// dispatchable method to the wrong slot.

public protocol CrossModuleSkippedMethodChildDelegate: CrossModuleSkippedMethodParentDelegate {
    // Intentionally empty — the test fires the inherited dispatchable method
    // through this child slot. If the cctor index ordering regresses, either
    // wrapper compile fails or the dispatched call lands on garbage.
}

public class CrossModuleSkippedMethodDelegateSource {
    public weak var childDelegate: CrossModuleSkippedMethodChildDelegate?
    public var lastSlotFired: Int32 = 0

    public init() {}

    /// Dispatches the dispatchable parent method through the child-typed
    /// slot. The skipped method sits at slot 0; this one must land at slot 1
    /// on both Swift and C# sides AND in the cctor's assignment.
    public func fireDispatchableViaChild(value: Int32) {
        if let d = childDelegate {
            lastSlotFired = 1
            d.dispatchableAfterSkippedMethod(value: value)
        } else {
            lastSlotFired = 0
        }
    }
}
