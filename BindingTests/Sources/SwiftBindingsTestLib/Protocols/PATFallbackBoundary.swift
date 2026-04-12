// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - PATFallbackBoundary (fix #7)
//
// Synthetic fixture for commit 4235d568's "PAT / Self-requirement protocol
// fallback to object" branch in ExistentialHandler.GetPublicExistentialType()
// at src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs:459-474.
//
// Protocols with associated types emit a generic interface (I{Name}<TSelf>)
// that can't be referenced without type arguments. Fix #7 rewrites call sites
// that would have produced an invalid `IReadOnlyList<ITip>` / `ITip? Foo_Get()`
// reference to use `object` instead. The fix was driven by TipKit and
// WeatherKit real-world regressions.
//
// Existing protocol tests cover `any ValueProviding` at parameter position,
// but that protocol has no associated types — it falls through the fix #7
// branch and ends up as `IValueProviding`. The interesting boundary is
// "method takes `any PATProtocol` at parameter position AND dispatches to a
// member on the conforming type that does NOT reference the associated type".
// That is the TipKit shape in the wild. Without fix #7 the emitted C#
// signature would try to reference `ITaggedAssociator<TSelf>` at a call site
// that has no TSelf in scope and fail to compile.

/// Protocol with an associated type. The `tag` property is deliberately
/// independent of the associated type so it can be dispatched through an
/// existential container — that is what makes fix #7's `object` fallback
/// actually work at runtime, not just at the type-checker boundary.
public protocol TaggedAssociator {
    associatedtype Item

    /// Type-specific tag that proves dispatch landed on the concrete conformer.
    var tag: String { get }

    /// Uses the associated type. Not called through the existential — it only
    /// exists to force `hasAssociatedTypes=true` in the TypeDatabase record
    /// so `ExistentialHandler.GetPublicExistentialType()` returns `"object"`.
    func process(_ item: Item) -> Int32
}

/// First conformer. `Item = Int32`.
public struct IntTaggedAssociator: TaggedAssociator {
    public typealias Item = Int32

    public init() {}

    public var tag: String { return "int-tagged-associator" }

    public func process(_ item: Int32) -> Int32 { return item * 2 }
}

/// Second conformer. `Item = String`. Different tag proves dispatch really
/// routes to the underlying concrete type, not to a default implementation.
public struct StringTaggedAssociator: TaggedAssociator {
    public typealias Item = String

    public init() {}

    public var tag: String { return "string-tagged-associator" }

    public func process(_ item: String) -> Int32 { return Int32(item.count) }
}

/// Free function that takes an existential PAT at the parameter position.
/// Fix #7 must lower this into a C# function whose parameter type is the
/// literal `object`. The dispatched `.tag` must come back unchanged — this
/// is the assertion that proves the fallback actually works at runtime.
public func readTaggedAssociator(_ assoc: any TaggedAssociator) -> String {
    return assoc.tag
}
