// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A class whose only Swift-tip initializers are a favored `init(_: any P, …)` and a
// `@_disfavoredOverload` `some P` twin, each with an unbindable defaulted parameter (an optional
// existential of a protocol with an associated type) and a defaulted closure. The `some P` twin's
// concrete specializations leave both defaults to Swift, which then resolves the call to the
// favored `any P` declaration, so those specializations are refused rather than bound to a
// declaration they don't name. The `any P` initializer itself binds with its defaults trimmed,
// so the type stays constructible, and the route each initializer records proves which Swift
// declaration ran.

public protocol PopoverTip {
    var title: String { get }
}

public struct BasicPopoverTip: PopoverTip {
    public var title: String
    public init(title: String) { self.title = title }
}

/// A type-erased tip, the conformer a C# caller would specialize the `some P` twin for.
public struct AnyPopoverTip: PopoverTip {
    public var title: String
    public init(_ tip: any PopoverTip) { title = tip.title }
}

public protocol PopoverAnchor {
    associatedtype Region
    var region: Region { get }
}

public final class TipPopoverHost {
    public let route: String
    public private(set) var anchored: Bool
    private let actionHandler: (Int) -> Void

    public init(_ tip: any PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        route = "any:" + tip.title
        anchored = anchor != nil
        self.actionHandler = actionHandler
    }

    @_disfavoredOverload
    public init(_ tip: some PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        route = "some:" + tip.title
        anchored = anchor != nil
        self.actionHandler = actionHandler
    }
}
