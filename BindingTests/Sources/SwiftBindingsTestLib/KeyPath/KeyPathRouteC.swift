// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Session 6c Route C fixture
//
// Validates the per-(conformer x distinct projectable V) Sort overload shape that
// `KeyPathBagValueSpecializationEmitter` produces. The Swift-side `sort<V>(by:)` is
// the "open-V" method CSM cannot specialize directly; Route C suppresses the
// open-V emission and replaces it with one closed `Sort(KeyPath<Bag, V>, Bool)`
// overload per distinct projectable Value type on the conformer's bag.
//
// Phase 2 scope: class generic parent. The mutating-struct receiver ABI lands in
// Phase 3 alongside the MusicKit wiring. The Swift method records a description
// of (keypath, ascending) so the C# tests can verify the dispatch arrived and
// the typed downcast produced the right closed shape.
//
// Bag shape: nested struct on the conformer (mirrors Session 4's
// MockBookSession4.LibraryFilter pattern). The bag-walker resolves the conformer's
// associated-type `SortBag` to the nested decl by short-name match.
//
// **Observation pattern**: side effects flow into a NON-GENERIC tracker class
// (`RouteC_SortTracker`) instead of into instance properties on the generic
// parent. The wrapper-emitter's property-getter pattern (`as! any _SBW_PG_*`)
// crashes when the receiver is a constrained generic class
// (`<Item: RouteC_Filterable>`) — Swift's runtime conformance lookup for the
// retroactive conformance fails to evaluate the conditional. That's a separate
// wrapper-emitter bug outside Session 6c scope; tracked in
// `src/docs/Future/property-getter-constrained-generic.md`. Static properties
// on a non-generic class read through the normal class-static path and don't
// trip the same code path.

public protocol RouteC_Filterable {
    associatedtype SortBag
}

public class RouteC_Album: RouteC_Filterable {
    public struct SortBag {
        public var title: String = ""
        public var year: Int = 0
        public var isAvailable: Bool = false
        public init() {}
    }
    public init() {}
}

public final class RouteC_SortTracker {
    public static var lastDescription: String = ""
    public static var lastKeyPathHash: Int = 0
    public static var lastAscending: Bool = false

    public static func reset() {
        lastDescription = ""
        lastKeyPathHash = 0
        lastAscending = false
    }
}

public class RouteC_GenericRequest<Item: RouteC_Filterable> {
    public init() {}

    // The open-V method. Route C replaces this with a closed-V overload per
    // distinct projectable Value type on `Item.SortBag`. `final func` bypasses
    // the `Tj` vtable thunk so the @_cdecl trampoline dispatches directly.
    //
    // Records into `RouteC_SortTracker` (a non-generic class) so the C# tests
    // can observe the dispatch without crossing the broken property-getter
    // wrapper path on the constrained generic parent.
    public final func sort<V>(by keyPath: KeyPath<Item.SortBag, V>, ascending: Bool) {
        RouteC_SortTracker.lastKeyPathHash = keyPath.hashValue
        RouteC_SortTracker.lastAscending = ascending
        RouteC_SortTracker.lastDescription = ascending ? "asc" : "desc"
    }
}
