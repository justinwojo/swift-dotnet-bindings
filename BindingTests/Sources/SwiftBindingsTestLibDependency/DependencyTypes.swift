// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cross-Module Types

/// Protocol for cross-module conformance testing.
/// Main test library types conform to this protocol to test
/// cross-module protocol conformance in generated bindings.
public protocol DependencyProtocol {
    var identifier: String { get }
    func describe() -> String
}

/// Frozen struct from the dependency module.
/// Used as parameter/return type in main test library functions
/// to test cross-module type references.
@frozen
public struct DependencyPoint {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    public func distanceFromOrigin() -> Double {
        return (x * x + y * y).squareRoot()
    }

    public func translated(dx: Double, dy: Double) -> DependencyPoint {
        return DependencyPoint(x: x + dx, y: y + dy)
    }
}

/// Non-frozen struct from the dependency module.
/// Tests cross-module opaque type handling.
public struct DependencyConfig {
    public var name: String
    public var version: Int32

    public init(name: String, version: Int32) {
        self.name = name
        self.version = version
    }

    public func summary() -> String {
        return "\(name) v\(version)"
    }
}

/// Class from the dependency module.
/// Tests cross-module class reference handling.
public class DependencyService {
    public var name: String
    public var isActive: Bool

    public init(name: String, isActive: Bool = true) {
        self.name = name
        self.isActive = isActive
    }

    public func status() -> String {
        return isActive ? "\(name): active" : "\(name): inactive"
    }
}

/// Enum from the dependency module.
/// Used for cross-module enum parameter/return testing.
@frozen
public enum DependencyStatus: Int32 {
    case unknown = 0
    case pending = 1
    case active = 2
    case inactive = 3

    public var label: String {
        switch self {
        case .unknown: return "Unknown"
        case .pending: return "Pending"
        case .active: return "Active"
        case .inactive: return "Inactive"
        }
    }
}

// MARK: - Free Functions

/// Creates a DependencyPoint.
public func makeDependencyPoint(x: Double, y: Double) -> DependencyPoint {
    return DependencyPoint(x: x, y: y)
}

/// Creates a DependencyConfig.
public func makeDependencyConfig(name: String, version: Int32) -> DependencyConfig {
    return DependencyConfig(name: name, version: version)
}

/// Creates a DependencyService.
public func makeDependencyService(name: String) -> DependencyService {
    return DependencyService(name: name)
}

/// Accepts a DependencyProtocol conformant and returns its description.
public func describeDependency(_ dep: some DependencyProtocol) -> String {
    return dep.describe()
}

// MARK: - Subclass-Only Existential Conformance (AnchorEntity / HasAnchoring shape)
//
// Reproduces RealityKit's `AnchorEntity : Entity, HasAnchoring`: the BASE class
// conforms to one cross-module member protocol (so it emits its own
// `IExistentialBoxable` baked with `Create<Base, _>`), and a subclass in another
// module adds a SECOND cross-module member protocol the base does NOT conform to.
// Boxing the subclass as that second protocol must dispatch `Create<Subclass, _>`;
// the inherited `Create<Base, _>` would request the non-existent
// `Base : DependencyAnchorMarker` witness and throw at box time.

/// Marker protocol the BASE entity conforms to. Cross-module and has a member, so
/// the C# interface stub is skipped but the conformance descriptor is still emitted —
/// giving `DependencyMarkedEntity` its own `IExistentialBoxable` implementation.
public protocol DependencyBaseMarker {
    func baseMarkerTag() -> Int32
}

/// Marker protocol that ONLY the subclass conforms to. The base does not. Boxing a
/// subclass instance as `any DependencyAnchorMarker` is the crux of the AnchorEntity
/// shape — it must resolve the subclass's own conformance descriptor, not the base's.
public protocol DependencyAnchorMarker {
    func anchorMarkerName() -> String
}

/// Open base entity in the dependency module conforming to `DependencyBaseMarker` only.
/// A main-module subclass adds `DependencyAnchorMarker` on top of this.
open class DependencyMarkedEntity: DependencyBaseMarker {
    public let baseId: Int32
    public init(baseId: Int32) { self.baseId = baseId }
    public func baseMarkerTag() -> Int32 { return baseId }
}

/// Consumes an `any DependencyAnchorMarker` existential and returns its name. Drives
/// the subclass-only conformance dispatch end-to-end from the Swift consumption side.
public func describeAnchorMarker(_ marker: any DependencyAnchorMarker) -> String {
    return marker.anchorMarkerName()
}

// MARK: - Cross-Module Class Inheritance (Bug #14)

/// Open base class living in the dependency module. The main module defines a subclass —
/// the parser must resolve `DependencyBaseEntity` via the global TypeDatabase rather than
/// the local `_typeDecls` dictionary, otherwise the C# emitter flattens the hierarchy.
open class DependencyBaseEntity {
    public var label: String
    public init(label: String) {
        self.label = label
    }

    open func describe() -> String {
        return "Base[\(label)]"
    }

    open func tag() -> Int32 {
        return 0
    }
}

/// Mid-tier class — also in the dependency module — for testing 3-level cross-module chains.
/// A subclass in the main module derived from this exercises the SuperclassTypeName walk.
open class DependencyMidEntity: DependencyBaseEntity {
    public var midTag: Int32

    public init(label: String, midTag: Int32) {
        self.midTag = midTag
        super.init(label: label)
    }

    open override func describe() -> String {
        return "Mid[\(label):\(midTag)]"
    }

    open override func tag() -> Int32 {
        return midTag
    }
}

/// Polymorphic accept: takes a base reference, returns its describe() result.
/// The C# call site must accept any subclass without an explicit cast — that's the
/// usability symptom Bug #14 fixes.
public func describeBaseEntity(_ entity: DependencyBaseEntity) -> String {
    return entity.describe()
}

/// Polymorphic accept: returns the runtime tag through the base.
public func readBaseEntityTag(_ entity: DependencyBaseEntity) -> Int32 {
    return entity.tag()
}

// MARK: - Cross-Module Type Alias Support

/// Concrete token type A — analogous to a specific Token instantiation.
/// Used to test cross-module type alias resolution.
@frozen
public struct DependencyTokenA {
    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
    }

    public func describe() -> String {
        return "Token(\(identifier))"
    }
}

/// Concrete token type B — analogous to a different Token instantiation.
/// Used to test cross-module type alias resolution.
@frozen
public struct DependencyTokenB {
    public let identifier: Int32

    public init(identifier: Int32) {
        self.identifier = identifier
    }

    public func describe() -> String {
        return "Token(\(identifier))"
    }
}

// MARK: - Nested-Type / Property Name Collision (cross-module rename propagation)

/// Reproduces the nested-type / property name collision shape: a struct with a
/// nested enum whose name matches a property's PascalCase name. The generator's
/// `NameProvider.ApplyNestedTypeRenames` renames the nested type with a kind-aware
/// semantic suffix — enum -> `Kind` (`AlertType` -> `AlertTypeKind`) — and the property keeps its original
/// PascalCase name. The producer module persists the renamed C# name in its
/// emitted module-database XML so the consumer module resolves cross-module
/// references against the renamed name.
public struct DependencyContainer {
    public enum AlertType: String {
        case info
        case warning
        case critical
    }

    public let name: String
    public let alertType: AlertType

    public init(name: String, alertType: AlertType) {
        self.name = name
        self.alertType = alertType
    }

    public func describe() -> String {
        return "\(name)[\(alertType.rawValue)]"
    }
}

// MARK: - Cross-Module Inherited Delegate (justinwojo/swift-dotnet-bindings#40
//                                          cross-module variant)
//
// Lives in the dependency module so the consuming module can declare a child
// protocol that inherits this one *across module boundaries*. See
// SwiftBindingsTestLib/Protocols/InheritedDelegateDispatch.swift for the child
// protocol and the source class that dispatches through it.

public protocol CrossModuleParentDelegate: AnyObject {
    /// Inherited by child protocols in other modules; the bug surfaces when a
    /// C# class implements only the child interface and Swift dispatches this
    /// through the parent's witness table — populated by the parent proxy's
    /// cctor, which lives in the dependency module.
    func crossModuleDidNotify(value: Int32)
}

// MARK: - Cross-module CARRIER-SPLIT parent (dependency half)
//
// Dependency half of the cross-module carrier-split gate. This parent requires
// no NSObjectProtocol, so its umbrella conformance routes to the plain
// `EveryProtocol` carrier. The consuming module declares a child that refines
// this parent AND NSObjectProtocol (SwiftBindingsTestLib/Protocols/
// CrossCarrierInheritedProtocol.swift), so the child routes to the NSObject-rooted
// `EveryObjCProtocol` carrier. Because the parent lives in a *different* module,
// the emitter's cross-carrier suppression gate must resolve the parent's carrier
// across the module boundary — otherwise it silently misses the split and emits
// an unsatisfiable `extension EveryObjCProtocol: <child>` in the consuming module.
public protocol CrossCarrierCrossModuleParent: AnyObject {
    /// Reverse-dispatched into by a C# conformer through the parent's
    /// `EveryProtocol` witness. Returns a distinguishable value so the runtime
    /// test can prove the parent conformance survives the child's suppression.
    func crossCarrierLabel() -> String
}

// MARK: - Transitive Cross-Module Ancestor Chain
//
// A local child in another module inherits CrossModuleTransitiveParentDelegate,
// which itself inherits CrossModuleTransitiveGrandparentDelegate. The child
// proxy's cctor must populate vtable storage for BOTH the direct cross-module
// parent AND its cross-module grandparent — that's the H1 "transitive
// ancestor walk" gate. A new dedicated chain (rather than retrofitting
// CrossModuleParentDelegate) keeps the existing cross-module child test from
// gaining a new method requirement on its C# impl.

public protocol CrossModuleTransitiveGrandparentDelegate: AnyObject {
    /// Two levels above the local child. Pre-H1-fix the BFS only walked the
    /// child's direct InheritedProtocols, so this grandparent's
    /// `_p_vtable` global was never populated and Swift force-unwrapped it.
    func crossModuleGrandparentDidNotify(value: Int32)
}

public protocol CrossModuleTransitiveParentDelegate: CrossModuleTransitiveGrandparentDelegate {
    /// One level above the local child. Always populated (direct parent),
    /// even pre-H1 — kept here so a regression in `_p_vtable` population
    /// for the direct parent shows up as a separate failure from the
    /// grandparent gate.
    func crossModuleParentDidNotify(value: Int32)
}

// MARK: - Cross-Module Parent With Non-Dispatchable Closure Property
//
// The cross-module C# vtable struct must apply the SAME membership filter
// EveryProtocolEmitter applies to its Swift wrapper vtable struct, otherwise
// the two layouts diverge and the wrapper writes the non-closure method
// pointer into a slot the C# side reads as a different member. The closure
// property here is non-dispatchable (closures don't go through the protocol
// witness table) — both sides must skip it.

public protocol CrossModuleClosurePropertyParentDelegate: AnyObject {
    /// Non-dispatchable closure property: skipped by both Swift's wrapper
    /// vtable struct AND the C# cross-module-parent vtable struct. If either
    /// side keeps the slot, the layout misaligns and `nonClosureDidNotify`
    /// receives a corrupted function pointer at dispatch time.
    /// Named uniquely (not `handler`) to avoid cross-protocol property-name
    /// collisions with `HasCallbackDelegate.handler`, which Swift cannot
    /// reconcile on a single conforming type.
    var closureCallback: ((Int32) -> Void)? { get set }

    /// Dispatchable method requirement: stays in both vtables. Used as the
    /// payload of the inherited-dispatch test — if the layouts diverge,
    /// invoking this through the child slot crashes or produces garbage.
    func nonClosureDidNotify(value: Int32)
}

// MARK: - Cross-Module Parent With Skipped Method Before Dispatchable Method
//
// The cross-module parent cctor in the consuming module previously skipped
// filtered methods BEFORE incrementing the slot index, while both the C#
// vtable struct (Vtables.cs) and the Swift wrapper struct (EveryProtocolEmitter)
// increment the index FIRST and then skip the field. The struct emitters
// produce a stable layout; the cctor's pre-fix ordering produced shifted slot
// assignments. Result: the cctor would try to assign the dispatchable method
// to slot `idx-1`, which either fails to compile (field name mismatch) or, if
// names happened to collide, wired the wrong function pointer.
//
// This protocol exercises that gap: a two-closure method (filtered by
// `ProtocolVtableMembers.IncludesMethod` via `IsDispatchableClosureMethod`'s
// "exactly one dispatchable closure param" gate — a per-method skip that
// does NOT poison the whole protocol the way `HasOnlyMethodLevelGenerics`
// would via `IsMixedGenericProtocol`) declared BEFORE a dispatchable method.
// Layouts only stay in lock-step if the cctor uses the same
// increment-then-filter ordering as the struct emitters.

// MARK: - Cross-Module Property Name + Type Collision Gate
//
// A protocol in the dependency module declares a property whose name collides
// with a property on an unrelated LOCAL protocol in the main module, but with
// a different type. The main module's EveryProtocol must conform to BOTH (this
// parent comes in via a local protocol that inherits it; the unrelated local
// is suitable on its own). Without the union-aware property-type-count gate in
// ModuleHandler.EmitEveryProtocolConformances, both would emit bodies on
// EveryProtocol and swiftc would reject with "invalid redeclaration of
// 'crossModuleConflictedId'". The gate drops the conflicting local; this
// cross-module parent stays so its own conformance still emits.
public protocol CrossModuleConflictingPropertyParent: AnyObject {
    var crossModuleConflictedId: Int32 { get }
    func crossModuleConflictingParentNotify(value: Int32)
}

// MARK: - Cross-Module Member-Kind (var-vs-func) Collision Gate
//
// Same shape as bug 3 (var label / func label() rejected by swiftc on the
// same nominal type) but with the property living in the dependency module
// and a `func` of the same base name living on an unrelated local protocol.
// Without the union-aware member-kind gate in
// ModuleHandler.EmitEveryProtocolConformances, the local's `func crossModuleLabel()`
// and this parent's `var crossModuleLabel` would both emit on EveryProtocol
// and swiftc would reject. The gate drops the function-side protocol from
// whichever list (local or parent) carries it.
public protocol CrossModuleMemberKindPropertyParent: AnyObject {
    var crossModuleLabel: Int32 { get }
    func crossModuleMemberKindParentNotify(value: Int32)
}

// MARK: - Cross-Module Inverse Member-Kind (dep func vs local var) + Same-Module-Hop Cascade
//
// Inverse direction of the gate above: the cross-module parent contributes the
// METHOD side (`func crossModuleInverseLabel() -> Int32`) and an unrelated local
// protocol contributes the PROPERTY side (`var crossModuleInverseLabel: Int32`).
// The member-kind gate drops the parent (function side); the cascade-drop step
// must then walk same-module-hop chains so a local grandchild whose only path
// to the dropped parent goes through a same-module intermediate is also
// removed from `suitableProtocols`. Without the same-module-hop walk in
// `TransitivelyInheritsCrossModuleParent`, only direct local children would
// be caught, and a `LocalGrandchild : LocalChild : DepInverseParent` chain
// would emit an `extension EveryProtocol: LocalGrandchild` whose inherited
// witness body has been removed — same C#/Swift-surface mismatch the property
// cascade prevents.
public protocol CrossModuleInverseMemberKindParent: AnyObject {
    func crossModuleInverseLabel() -> Int32
    func crossModuleInverseParentNotify(value: Int32)
}

public protocol CrossModuleSkippedMethodParentDelegate: AnyObject {
    /// Two-closure method — `ProtocolVtableMembers.IncludesMethod` filters
    /// it out via `IsDispatchableClosureMethod`'s "exactly one dispatchable
    /// closure param" gate. Per-method skip (not whole-protocol like
    /// method-level-generic), so the dispatchable method below stays in the
    /// vtable. Both the Swift wrapper struct and the C# cross-module-parent
    /// struct reserve no field for it but still consume slot index 0, so the
    /// dispatchable method that follows lands at slot 1, not 0.
    func twoClosureSkip(first: (Int32) -> Void, second: (Int32) -> Void)

    /// Dispatchable method: appears at slot index 1 in both the Swift
    /// wrapper vtable struct and the C# cross-module-parent vtable struct.
    /// The cctor must also assign it to slot 1; pre-fix it tried slot 0.
    func dispatchableAfterSkippedMethod(value: Int32)
}

// MARK: - Finding 33 — per-module EveryProtocol metadata (dependency-module side)
//
// `DepReverseValue` is an OPAQUE (non-`AnyObject`) reverse-dispatch protocol living in the
// dependency module. When a C# class conforms to it, the auto-wrapped proxy builds an
// opaque existential whose metadata word comes from THIS module's
// `NativeMethods.GetEveryProtocolMetadata()` (the per-proxy `s_everyProtocolMetadata`
// static). The companion main-module protocol (`ReverseInvariantAlpha`) does the same with
// the MAIN module's accessor.
//
// Pre-Finding-33 the C# `EveryProtocol` type held one process-global metadata latch, so
// whichever module initialised first won and the other module's opaque existentials were
// stamped with the wrong type metadata. A single C# object conforming to BOTH the main and
// dependency opaque protocols and being dispatched through BOTH in the same process is the
// scenario that exposes the latch: the dependency existential is STORED (copied via its
// value-witness table, which is keyed on the metadata word) and then dispatched, so a
// wrong-module metadata word corrupts the copy/destroy rather than merely mislabelling a
// type.

public protocol DepReverseValue {
    /// Returns `value + 3000` so the C# test can prove the dependency view serviced the
    /// call with this module's metadata.
    func depValue(_ value: Int32) -> Int32
}

/// Stores and dispatches a dependency-module opaque existential. The store copies the
/// existential through its (per-module) value-witness table; `roundTripStored` then
/// dispatches into it. Used by the Finding-33 two-module fixture.
public class DepReverseValueHarness {
    private var stored: (any DepReverseValue)?

    public init() {}

    /// Direct dispatch (no storage): returns `value + 3000` for a correct resolver.
    public func pingDepValue(_ d: any DepReverseValue, value: Int32) -> Int32 {
        return d.depValue(value)
    }

    /// Store the existential (value-witness copy through this module's metadata) then
    /// dispatch into it. A wrong-module metadata word would corrupt the stored copy.
    public func roundTripStored(_ d: any DepReverseValue, value: Int32) -> Int32 {
        stored = d
        let result = stored?.depValue(value) ?? -1
        stored = nil
        return result
    }
}
