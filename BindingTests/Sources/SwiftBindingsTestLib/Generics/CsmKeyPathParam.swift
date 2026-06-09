// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - CSM KeyPath-as-method-param fixture
//
// This fixture adds `KeyPathFamily` as a first-class `ParamAbiCategory` so the
// CSM (Concrete Specialization Machinery) emitter can accept `KeyPath<Root, Value>`
// parameters on PAT-constrained generic-parent methods and route them through
// `DangerousGetHandle()` rather than the broken `((ISwiftObject)x).SwiftHandle`
// PayloadHandle arm (the KeyPath family is SafeHandle-backed, not ISwiftObject).
//
// This fixture isolates that wiring from the more intricate τ_0_0-rooted
// substitution work. The KeyPath's Root is a **top-level concrete struct**
// (`CsmKp_ConcreteFilter`) rather than `T.Filter` — so the pairing-generic
// substitution gate (`HasNonGenericParamReferencingGeneric`) does NOT fire on
// this fixture by design. What it DOES exercise:
//
//   * `MethodClosureBridge.ClassifyParam` returns `KeyPathFamily` for the param.
//   * `AreNonGenericParamsCompatible` accepts the pairing via
//     `IsAbiCategoryPassableForCsm` (the CSM-only superset that adds KeyPathFamily
//     on top of the closure-bridge-narrow `IsAbiCategoryPassable`).
//   * CSM emits per-conformer extension classes (CsmKp_BagCsmKp_ConformerA…CsmExtensions
//     and CsmKp_BagCsmKp_ConformerB…CsmExtensions), each containing a
//     `Count(matching:)` method whose param type is `KeyPath<CsmKp_ConcreteFilter, string>`.
//   * The P/Invoke call site uses `csName.DangerousGetHandle()` (NOT
//     `((ISwiftObject)csName).SwiftHandle`).
//   * The Swift `@_cdecl` wrapper receives the KeyPath as `UnsafeRawPointer`
//     and reconstructs it via `Unmanaged<KeyPath<CsmKp_ConcreteFilter, String>>
//     .fromOpaque(_kp).takeUnretainedValue()`.
//
// A later change lifts the τ_0_0-rooted shape (`KeyPath<T.Filter, V>`).

// MARK: PAT + closed conformers (the parent-bag plumbing)

public protocol CsmKp_Filterable {
    static var displayName: Swift.String { get }
}

public struct CsmKp_ConformerA: CsmKp_Filterable {
    public init() {}
    public static let displayName: Swift.String = "CsmKp_ConformerA"
}

public struct CsmKp_ConformerB: CsmKp_Filterable {
    public init() {}
    public static let displayName: Swift.String = "CsmKp_ConformerB"
}

// MARK: Top-level concrete filter — the KeyPath Root
//
// Deliberately NOT nested under a conformer. With this shape, the `Bag<T>.count`
// param tree is `KeyPath<CsmKp_ConcreteFilter, String>` — no `τ_0_0` reference.
// A future change will introduce a conformer-nested variant that exercises substitution.

public struct CsmKp_ConcreteFilter {
    public var title: Swift.String
    public init(title: Swift.String) { self.title = title }
}

// MARK: Generic-parent bag — the CSM demand signal
//
// `count(matching:)` takes a concrete-rooted KeyPath. CSM must emit per-conformer
// extension classes (one for ConformerA, one for ConformerB) that each carry a
// concrete Count(matching:) method.

public struct CsmKp_Bag<T: CsmKp_Filterable> {
    public init() {}

    public func count(matching keyPath: KeyPath<CsmKp_ConcreteFilter, Swift.String>) -> Swift.Int {
        // Body is observable so the round-trip test can verify the keypath was reached and applied.
        let probe = CsmKp_ConcreteFilter(title: T.displayName)
        return probe[keyPath: keyPath].count
    }
}

// MARK: KeyPath factory — origination of the typed KP
//
// `CsmKp_ConcreteFilter` is top-level (not conformer-nested), so the
// typed-singleton-emission demand walk does not fire for it (the typed-singleton
// emitter walks only conformer-nested bags). C# instead calls this factory to
// obtain a `KeyPath<CsmKp_ConcreteFilter, String>` via the standard OUT path.

public class CsmKp_KeyPathFactory {
    public class func makeTitlePath() -> KeyPath<CsmKp_ConcreteFilter, Swift.String> {
        \CsmKp_ConcreteFilter.title
    }
}
