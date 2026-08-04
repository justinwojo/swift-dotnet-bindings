// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic protocol existentials (`any P<X>`)
//
// Layer A coverage for parameterised protocols used as constrained existentials
// (Cases 1 and 2): a parameterised protocol used as a constrained existential.
// Before the fix the projection collapsed to `Swift.AnyType` for any protocol whose
// `GenericParameters.Count > 0`, even when every argument was a concrete
// `NamedTypeSpec` — silently dropping the strongly-typed surface that
// real-world Apple SDK APIs (AsyncSequence<Sample>, EventStream<UIEvent>, …)
// rely on.
//
// The fixture pins the concrete-arg case (`any LabelledContainer<String>`)
// where every generic argument resolves through the type database, so the
// projection MUST round-trip as `ILabelledContainer<string>` rather than
// `AnyType`. The associated-type case (`any LabelledContainer<Self.Element>`)
// is already exercised by other fixtures and stays at AnyType.

/// Constrained-protocol with one concrete same-type generic argument.
public protocol LabelledContainer<Label> {
    associatedtype Label
    var label: Label { get }
    func describeLabel() -> String
}

public struct StringLabel: LabelledContainer {
    public typealias Label = String
    public let label: String
    public init(label: String) { self.label = label }
    public func describeLabel() -> String {
        return "label=\(label)"
    }
}

/// Accepts a constrained existential. The bound is concrete (`String`), so
/// the C# projection must surface `ILabelledContainer<string>`.
///
/// `any P<X>` (constrained / parameterized existentials) requires runtime
/// support that ships with iOS 16 / macOS 13 / tvOS 16. Without these
/// availability annotations the Swift wrapper compile fails on iOS 15
/// deployment targets with "runtime support for parameterized protocol
/// types is only available in iOS 16.0.0 or newer". The wrapper generator
/// propagates the annotation onto the corresponding `@_cdecl` symbol.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func describeStringLabel(_ container: any LabelledContainer<String>) -> String {
    return container.describeLabel()
}

/// Returns a constrained existential. Same lowering — strongly typed, not
/// collapsed to AnyType.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func makeStringLabel(_ value: String) -> any LabelledContainer<String> {
    return StringLabel(label: value)
}

// MARK: - Constrained-existential CONSTRUCTOR (emitted shape diverges from the declared one)
//
// A constructor taking a constrained existential is bound by a dedicated bridge that cannot keep
// the declared parameter type: the C# side cannot statically tell a class conformer from a struct
// conformer, so the parameter is erased to the broadest common type (`ISwiftObject`), and the
// member is written under the TYPE's name rather than under any method name. Neither half of the
// emitted declaration matches the declared one, which makes this the constructor counterpart of
// the closure-bridge divergence in Closures/ReshapedPublicSignature.swift.
//
// Provenance: a document-scanning SDK's `init(analyzer:settings:)`, whose analyzer parameter is a
// parameterized-protocol existential, hit exactly this shape.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public final class LabelledContainerSink {
    private let container: any LabelledContainer<String>
    private let tag: Int32

    public init(container: any LabelledContainer<String>, tag: Int32) {
        self.container = container
        self.tag = tag
    }

    public func describe() -> String {
        return "\(tag):\(container.describeLabel())"
    }
}

// MARK: - Open-PAT (generic-conformer-bound) negative fixture
//
// Pins the open-PAT exclusion gate in the closed-constrained PAT projection:
// when the conforming type is itself generic and binds the protocol's associated
// type to its own type parameter (here `Label == U`), the closed interface
// `ILabelledContainer<U>` depends on a conformer-side parameter and must NOT be
// emitted in the implements list — open PATs still flow through the typeof(object)
// PAT box. The C# binding for this fixture must surface as
// `OpenLabelledContainer<U> : ISwiftObject, ISwiftStruct, IDisposable, IExistentialBoxable`
// WITHOUT `ILabelledContainer<U>`.
//
// Type renamed away from `GenericContainer<U>` to avoid a collision with the
// existing `GenericContainer<T: SearchableItem>` fixture in MethodLevelGenerics.swift.
public struct OpenLabelledContainer<U>: LabelledContainer {
    public typealias Label = U
    public let label: U
    public init(label: U) { self.label = label }
    public func describeLabel() -> String {
        return "generic"
    }
}

// MARK: - Constrained-existential VARIADIC coverage
//
// A result-builder `buildBlock` whose variadic parameter is a constrained existential:
// `(any LabelledContainer<String>)...`. This is the intersection of two features —
// SE-0346 parameterized-protocol existentials AND variadic packs — and is the deepest
// case of the demangle-based variadic-detection regression. `any LabelledContainer<String>`
// mangles through the `ConstrainedExistential` (`XP`) node with an inline requirement list
// (`Self.Label == String`), whose base is `ConstrainedExistentialSelf` (the `s` marker in
// `Rts_`). Until the demangler learned that node, ANY such signature threw during demangle
// (caught + logged → null reduction), silently disabling the demangled "d"-marker variadic
// signal. swift-api-digester renders the parameter as a plain `[any LabelledContainer<String>]`
// with no trailing "...", so the "d" marker is the only per-overload variadic signal — the same
// lost-signal failure mode as the plain-existential `(any VariadicItem)...` case, one layer deeper.
// `Int` return keeps the round-trip focused on variadic detection, not existential-array returns.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public struct LabelledContainerBuilder {
    public static func buildBlock(_ items: (any LabelledContainer<String>)...) -> Int {
        return items.count
    }

    public static func buildBlock() -> Int {
        return 0
    }
}
