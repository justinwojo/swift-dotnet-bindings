// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the conformance-keep ↔ member-emission AGREEMENT gate.
//
// Two separate decisions used to be made by two gates that did not agree:
//   (a) "keep the protocol in the concrete type's implements-list" — driven by the
//       lightweight `MemberEmissionValidator.CanEmit*` helpers; and
//   (b) "actually emit the witness member" — driven by the full member-emission
//       pipeline (`MemberValidationPipeline.Validate*Emission`), which carries
//       strictly more skip gates.
//
// `AsyncTagProvider` is a PLAIN (non-PAT) protocol, so it projects to a clean C#
// interface `IAsyncTagProvider` with `string Label()` and `Task<int> CurrentTagAsync()`.
//
// `GenericTagBox<Element>` is the trap: its `currentTag()` witness is an `async`
// method on an UNSPECIALIZED generic parent with no method-own generics and no
// specialization hints — so the emitter SKIPS it (an async wrapper on a generic
// parent can't supply the parent's type metadata + self through a direct
// CallConvSwift P/Invoke). Gate (a) passed the witness (CanEmitMethod has no
// async-on-generic-parent gate) while gate (b) skipped it, so the binding used to
// emit `GenericTagBox<Element> : IAsyncTagProvider` with no `CurrentTagAsync`
// member → CS0535, failing the WHOLE module compile. The fix makes (a) consult
// the same pipeline as (b): the conformance is dropped, `GenericTagBox<Element>`
// degrades gracefully (it keeps its emittable `Label()` and just loses the
// protocol projection), and the rest of the module still emits.
//
// `ConcreteTagBox` is the discrimination control: the SAME protocol witnessed on a
// NON-generic parent IS emittable, so its conformance must be KEPT and round-trip
// at runtime — proving the fix is surgical (it drops only the conformance the
// emitter genuinely can't satisfy, never the interface itself).

/// Plain (non-PAT) protocol → projects to a clean C# `IAsyncTagProvider` interface.
public protocol AsyncTagProvider {
    /// Always-emittable sync requirement (proves the generic conformer keeps its
    /// non-async surface even after the async-driven conformance drop).
    func label() -> String

    /// Async requirement. Emittable on a non-generic parent; SKIPPED on an
    /// unspecialized generic parent — the witness that forces the agreement gate.
    func currentTag() async -> Int32
}

/// Non-generic conformer: both requirements are emittable, so the conformance is
/// KEPT and `ConcreteTagBox` implements `IAsyncTagProvider`.
public final class ConcreteTagBox: AsyncTagProvider {
    private let name: String
    private let tag: Int32

    public init(name: String, tag: Int32) {
        self.name = name
        self.tag = tag
    }

    public func label() -> String { name }

    public func currentTag() async -> Int32 { tag }
}

/// Generic conformer: `currentTag()` is async on an unspecialized generic parent →
/// the emitter drops it → the whole `AsyncTagProvider` conformance must be dropped
/// (otherwise CS0535). `Label()` still emits as a plain method on the generic type.
public final class GenericTagBox<Element>: AsyncTagProvider {
    private let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }

    public func label() -> String { "generic" }

    public func currentTag() async -> Int32 { tag }
}

/// Closed-generic factory so a `GenericTagBox<Int>` instantiation exists for the
/// binding to reify (the conformance-drop is observable on the emitted type).
public func makeGenericTagBox(tag: Int32) -> GenericTagBox<Int> {
    return GenericTagBox<Int>(tag: tag)
}
