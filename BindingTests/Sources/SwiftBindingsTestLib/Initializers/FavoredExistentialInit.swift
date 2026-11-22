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

/// The same favored/disfavored pair with a trailing `line: UInt = #line`. The trimmed form leaves the
/// anchor and the line to Swift: a trim that counted `line` as one of the defaults to drop kept the
/// unbindable anchor and recovered nothing, leaving the type without a Swift-tip initializer.
public final class TipLineHost {
    public let route: String
    public let anchored: Bool
    public let line: UInt

    public init(_ tip: any PopoverTip, anchor: (any PopoverAnchor)? = nil, line: UInt = #line) {
        route = "any:" + tip.title
        anchored = anchor != nil
        self.line = line
    }

    @_disfavoredOverload
    public init(_ tip: some PopoverTip, anchor: (any PopoverAnchor)? = nil, line: UInt = #line) {
        route = "some:" + tip.title
        anchored = anchor != nil
        self.line = line
    }
}

/// A failable favored/disfavored pair on a class. The trimmed form's wrapper returns the nullable
/// retained pointer, so its factory needs no Optional helper from a primary emission that never ran:
/// it is recovered as a `TryCreate` that reports Swift's `nil` as false.
public final class TipGateHost {
    public let route: String
    public let anchored: Bool

    public init?(_ tip: any PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        if tip.title.isEmpty { return nil }
        route = "any:" + tip.title
        anchored = anchor != nil
    }

    @_disfavoredOverload
    public init?(_ tip: some PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        if tip.title.isEmpty { return nil }
        route = "some:" + tip.title
        anchored = anchor != nil
    }
}

/// The failable pair beside a labelled plain initializer of the same input type. The plain one is a
/// C# constructor and the favored failable one's trimmed form a `TryCreate` factory; the two are
/// different C# members, so the constructor must not cost the recovery.
public final class TipGateLabelledHost {
    public let route: String

    public init(fallback tip: any PopoverTip) {
        route = "fallback:" + tip.title
    }

    public init?(_ tip: any PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        if tip.title.isEmpty { return nil }
        route = "any:" + tip.title
    }

    @_disfavoredOverload
    public init?(_ tip: some PopoverTip, anchor: (any PopoverAnchor)? = nil, actionHandler: @escaping (Int) -> Void = { _ in }) {
        if tip.title.isEmpty { return nil }
        route = "some:" + tip.title
    }
}
