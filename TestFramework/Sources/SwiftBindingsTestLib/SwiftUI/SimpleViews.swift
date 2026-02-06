// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Simple SwiftUI Views for bridge parameter type testing.
// Each View exercises one parameter kind from the v2 bridge emitter.

import SwiftUI

/// Tests BoundEnum parameter kind.
public struct EnumParamView: View {
    public let style: AlertStyle

    public init(style: AlertStyle) {
        self.style = style
    }

    public var body: some View {
        Text("EnumParam: \(style.rawValue)")
    }
}

/// Tests BoundType (class) parameter kind.
public struct ClassParamView: View {
    public let model: SimpleModel

    public init(model: SimpleModel) {
        self.model = model
    }

    public var body: some View {
        Text("ClassParam: \(model.getValue())")
    }
}

/// Tests TypedClosure: (Int32) -> Bool parameter kind.
public struct TypedClosureView: View {
    public let onValue: (Int32) -> Bool

    public init(onValue: @escaping (Int32) -> Bool) {
        self.onValue = onValue
    }

    public var body: some View {
        Text("TypedClosure")
    }
}

/// Tests multi-arg TypedClosure: (Int32, Bool) -> Void parameter kind.
public struct MultiArgClosureView: View {
    public let onEvent: (Int32, Bool) -> Void

    public init(onEvent: @escaping (Int32, Bool) -> Void) {
        self.onEvent = onEvent
    }

    public var body: some View {
        Text("MultiArgClosure")
    }
}

/// Tests mixed parameters: BoundEnum + void closure + primitive.
public struct MixedParamView: View {
    public let style: AlertStyle
    public let onAction: () -> Void
    public let count: Int32

    public init(style: AlertStyle, onAction: @escaping () -> Void, count: Int32) {
        self.style = style
        self.onAction = onAction
        self.count = count
    }

    public var body: some View {
        Text("Mixed: \(style.rawValue), \(count)")
    }
}

/// Tests Optional<BoundEnum> parameter kind.
public struct OptionalEnumView: View {
    public let style: AlertStyle?

    public init(style: AlertStyle? = nil) {
        self.style = style
    }

    public var body: some View {
        Text("OptionalEnum: \(style?.rawValue ?? -1)")
    }
}

/// Tests Optional<BoundType> (class) parameter kind.
public struct OptionalClassView: View {
    public let model: SimpleModel?

    public init(model: SimpleModel? = nil) {
        self.model = model
    }

    public var body: some View {
        Text("OptionalClass: \(model?.getValue() ?? -1)")
    }
}
