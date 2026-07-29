// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Existentials in Container Positions
//
// An existential whose protocol is declared in a SIBLING module must project to a
// module-qualified C# interface name. A generated binding emits no `using` for a sibling
// binding's namespace, so a bare `IDependencyProtocol` written into this module's signatures
// is unresolvable in the consuming package and fails CS0246 — a failure that only shows up
// when the two modules are compiled as separate assemblies, which is exactly what this
// fixture pair does.
//
// The plain parameter position (see `describeAnyDependency`) already resolves the
// qualification through the method environment's module-aware existential oracle. Each
// CONTAINER translator below reaches the projection through a different oracle instance —
// enum-case associated values, bound-generic (array) arguments and closure parameter types
// each have their own — so each is an independent chance to drop the module segment.
// Provenance: observed as CS0246 on the enum-case factory methods and TryGet deconstructors
// of a third-party networking binding that referenced a sibling module's request-encoding
// protocol.
//
// A tuple carrying an existential element is deliberately absent: that shape is unsupported in
// either direction today (the tuple read-back marshals the element as `Swift.AnyType` and the
// tuple metadata is built from `Any`, not the existential container), so it exercises a
// marshalling gap rather than the qualification question this fixture is about.

/// Enum whose associated values are cross-module existentials, in the three shapes the
/// case-construction and case-inspection emitters render separately: a bare payload, a
/// bound-generic (array) payload, and a labelled multi-payload case.
public enum CrossModuleExistentialPayload {
    case single(any DependencyProtocol)
    case several([any DependencyProtocol])
    case labeled(tag: Int32, value: any DependencyProtocol)

    /// Non-trivial accessor so the case payloads are read back through Swift, not only
    /// constructed from C#.
    public func describePayload() -> String {
        switch self {
        case .single(let dep):
            return "single:\(dep.describe())"
        case .several(let deps):
            return "several:\(deps.count)"
        case .labeled(let tag, let value):
            return "labeled:\(tag):\(value.describe())"
        }
    }
}

/// Bound-generic (array) parameter whose element is a cross-module existential.
public func describeDependencyList(_ deps: [any DependencyProtocol]) -> String {
    return deps.map { $0.describe() }.joined(separator: "|")
}

/// Concrete type whose SUBSCRIPTS return cross-module existentials. The indexer's public type
/// and its getter conversion are resolved by their own projection instances — separate from the
/// method and property paths above — so each is an independent chance to drop the module segment
/// from the element interface.
public final class DependencyRegistry {
    private let entries: [any DependencyProtocol]

    public init(entries: [any DependencyProtocol]) {
        self.entries = entries
    }

    public var count: Int32 { Int32(entries.count) }

    /// Stored property whose type is a bound generic over a cross-module existential. The
    /// container-shaped property type is resolved by a projection that OVERRIDES the plain
    /// existential name, and its getter and setter bodies each convert through their own
    /// projection — three more places the module segment can go missing.
    public var pinned: [any DependencyProtocol] = []

    /// Scalar existential in return position.
    public subscript(index: Int32) -> any DependencyProtocol {
        return entries[Int(index)]
    }

    /// Bound-generic (array) of existentials in return position — the container arm of the same
    /// indexer projection. The key type differs from the scalar subscript above on purpose: two
    /// subscripts over the same parameter type collapse to one C# indexer and the second is dropped
    /// as a duplicate signature, which would leave this arm unexercised.
    public subscript(matching prefix: String) -> [any DependencyProtocol] {
        return entries.filter { $0.describe().hasPrefix(prefix) }
    }

    public func describeAll() -> String {
        return entries.map { $0.describe() }.joined(separator: "|")
    }
}

/// Protocol whose OWN members carry cross-module existentials, in scalar and container positions.
/// The `interface I…` declaration is rendered by the protocol emitter's private type-name oracles —
/// separate instances from the concrete-type paths above — so this is the position where a dropped
/// module segment lands inside an interface member signature rather than a class member.
public protocol DependencyAggregating {
    var dependencies: [any DependencyProtocol] { get }
    func merge(with others: [any DependencyProtocol]) -> String
    func primary() -> any DependencyProtocol
}

/// Concrete conformer so the interface has a Swift-side implementation to dispatch into.
public final class DependencyAggregator: DependencyAggregating {
    public let dependencies: [any DependencyProtocol]

    public init(dependencies: [any DependencyProtocol]) {
        self.dependencies = dependencies
    }

    public func merge(with others: [any DependencyProtocol]) -> String {
        return (dependencies + others).map { $0.describe() }.joined(separator: "+")
    }

    public func primary() -> any DependencyProtocol {
        return dependencies[0]
    }
}

/// Closure parameter whose own parameter type is a cross-module existential.
public func applyToDependency(
    _ dep: any DependencyProtocol,
    using body: (any DependencyProtocol) -> Void
) {
    body(dep)
}

/// Shapes whose C# surface is written by a SECOND emitter running alongside the primary
/// signature — a convenience overload rather than the member itself. Each of these builds its
/// own projection, so the primary member can qualify a cross-module existential correctly while
/// the companion overload still emits a bare interface name that does not resolve.
public final class DependencyLoader {
    private let entries: [any DependencyProtocol]

    public init(entries: [any DependencyProtocol]) {
        self.entries = entries
    }

    /// Completion-handler method: the generated binding grows a Task-returning convenience
    /// overload beside the callback signature. Both the overload's own parameter list (the
    /// non-closure `fallback`) and its `Task<…>` result type are projected separately from the
    /// primary — and the result type is additionally string-compared against the callback's
    /// projected type, so a disagreement there drops the overload silently rather than failing
    /// to compile.
    public func loadDependency(
        tag: Int32,
        fallback: any DependencyProtocol,
        completion: @escaping (any DependencyProtocol) -> Void
    ) {
        completion(entries.first ?? fallback)
    }

    /// A machine-width `Int` parameter makes the binding emit an extra `int`-taking convenience
    /// overload; the sibling existential parameter is re-projected for that overload's signature.
    public func describeAt(_ index: Int, fallback: any DependencyProtocol) -> String {
        guard index >= 0 && index < entries.count else { return fallback.describe() }
        return entries[index].describe()
    }

    /// Throwing closure parameter: the binding emits a simplified `Func`-taking overload beside
    /// the `SwiftResult`-returning primary. That overload re-projects its sibling NON-closure
    /// parameters through the same convenience-overload oracle the machine-width overload above
    /// uses, so it is a second consumer of the module segment on a different emitter.
    public func transformDependency(
        _ fallback: any DependencyProtocol,
        using transform: (Int32) throws -> Int32
    ) throws -> Int32 {
        return try transform(Int32(fallback.describe().count))
    }

    /// Async return whose element is a cross-module existential: the container conversion is
    /// written into the async completion callback body by its own projection, separate from the
    /// method's declared return type.
    public func fetchDependencies() async -> [any DependencyProtocol] {
        return entries
    }
}
