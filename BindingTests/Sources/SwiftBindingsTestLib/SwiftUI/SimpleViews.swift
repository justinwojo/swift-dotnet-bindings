// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Simple SwiftUI Views for bridge parameter type testing.
// Each View exercises one parameter kind from the v2 bridge emitter.

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
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

/// Tests TypedClosure with String argument: (String) -> Void.
public struct StringClosureView: View {
    public let onResult: (String) -> Void

    public init(onResult: @escaping (String) -> Void) {
        self.onResult = onResult
    }

    public var body: some View {
        Text("StringClosure")
    }
}

/// Tests TypedClosure with class argument: (SimpleModel) -> Void.
public struct ClassClosureView: View {
    public let onModel: (SimpleModel) -> Void

    public init(onModel: @escaping (SimpleModel) -> Void) {
        self.onModel = onModel
    }

    public var body: some View {
        Text("ClassClosure")
    }
}

/// Tests Optional<String> parameter kind.
public struct OptionalStringView: View {
    public let title: String?

    public init(title: String? = nil) {
        self.title = title
    }

    public var body: some View {
        Text("OptionalString: \(title ?? "nil")")
    }
}

/// Tests Optional<Closure> parameter kind.
public struct OptionalClosureView: View {
    public let callback: ((Int32) -> Void)?

    public init(callback: ((Int32) -> Void)? = nil) {
        self.callback = callback
    }

    public var body: some View {
        Text("OptionalClosure")
    }
}

/// Tests mixed String parameter + String closure.
public struct MixedStringView: View {
    public let title: String
    public let onResult: (String) -> Void

    public init(title: String, onResult: @escaping (String) -> Void) {
        self.title = title
        self.onResult = onResult
    }

    public var body: some View {
        Text("MixedString: \(title)")
    }
}

// MARK: - View Modifier Chain

/// Tests self-returning modifier detection and bridge emission.
public struct ModifiableView: View {
    public let title: String

    public init(title: String) {
        self.title = title
    }

    /// Parameterless modifier (bool toggle).
    public func highlighted() -> Self { return self }

    /// Single-param Double modifier.
    public func opacity(level: Double) -> Self { return self }

    /// Single-param Bool modifier.
    public func enabled(_ flag: Bool) -> Self { return self }

    /// Single-param modifier whose EXTERNAL argument label is a Swift keyword
    /// (`repeat`). The bridge must emit the modifier call label bare (`badge(repeat: …)`)
    /// — escaping a keyword argument label warns, and the C#-safe internal name (`count`)
    /// must not leak as the label. Gates the keyword-label modifier call path.
    public func badge(repeat count: Int32) -> Self { return self }

    /// Single-param modifier whose parameter is a two-way `Binding<Bool>`, not a value.
    /// Only the inner Bool crosses the bridge ABI, so the generated modifier call has to
    /// construct a Binding over the stored state — handing the stored value straight to a
    /// `Binding<Bool>` parameter does not type-check, which only the Swift compile of the
    /// generated bridge catches. Gates the Binding<T> modifier-parameter call path.
    public func toggled(_ binding: Binding<Bool>) -> Self { return self }

    public var body: some View {
        Text(title)
    }
}

// MARK: - Generic View Support

/// Tests generic view with View-constrained placeholder and two constructors:
/// - init(title:) where Placeholder == EmptyView  →  bridge selects this (concrete constraint)
/// - init(title:, @ViewBuilder placeholder: () -> Placeholder)  →  requires synthesized closure
public struct GenericPlaceholderView<Placeholder: View>: View {
    public let title: String

    public init(title: String) where Placeholder == EmptyView {
        self.title = title
    }

    public init(title: String, @ViewBuilder placeholder: () -> Placeholder) {
        self.title = title
    }

    public var body: some View {
        Text(title)
    }
}

/// Tests generic view where ALL init params are synthesizable (zero C# params).
/// Bridge synthesizes: PlaceholderOnlyView(content: { EmptyView() })
public struct PlaceholderOnlyView<Content: View>: View {
    public init(@ViewBuilder content: () -> Content) {}

    public var body: some View {
        Text("PlaceholderOnly")
    }
}

// MARK: - Two-Way State Binding

/// Tests updatable primitive + string params (no closures).
public struct UpdatableCounterView: View {
    public let count: Int32
    public let label: String

    public init(count: Int32, label: String) {
        self.count = count
        self.label = label
    }

    public var body: some View {
        Text("\(label): \(count)")
    }
}

/// Tests mixed updatable + closure params (title/isEnabled updatable, onTap closure).
public struct UpdatableMixedView: View {
    public let title: String
    public let isEnabled: Bool
    public let onTap: () -> Void

    public init(title: String, isEnabled: Bool, onTap: @escaping () -> Void) {
        self.title = title
        self.isEnabled = isEnabled
        self.onTap = onTap
    }

    public var body: some View {
        Text(title)
    }
}

// MARK: - Closure Non-Primitive Returns

/// Tests TypedClosure with String return: (Int32) -> String.
/// C# callback encodes returned string as UTF-8 native buffer; Swift decodes.
public struct StringReturnClosureView: View {
    public let transformer: (Int32) -> String

    public init(transformer: @escaping (Int32) -> String) {
        self.transformer = transformer
    }

    public var body: some View {
        Text(transformer(42))
    }
}

// MARK: - Lifecycle & Presentation

/// Tests lifecycle callbacks (onAppear/onDisappear) and universal modifiers.
/// Bridge always emits lifecycle params on Create factory.
public struct LifecycleTestView: View {
    public let title: String

    public init(title: String) {
        self.title = title
    }

    public var body: some View {
        Text(title)
    }
}

/// Tests TypedClosure with class return: (Int32) -> SimpleModel.
/// C# callback retains via Arc.Retain; Swift takes ownership via Unmanaged.takeRetainedValue.
public struct ClassReturnClosureView: View {
    public let factory: (Int32) -> SimpleModel

    public init(factory: @escaping (Int32) -> SimpleModel) {
        self.factory = factory
    }

    public var body: some View {
        Text("ClassReturn: \(factory(1).getValue())")
    }
}
#endif
