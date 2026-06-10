// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum case carrying an Optional<Closure> payload (audit item F16)
//
// The enum-case construction path projects an `Optional<Closure>` payload
// through `OptionalProjection.GetParameterPlan` generic fall-through, with no
// `ContainsClosureTypeSpec` guard on the construction-path factory calls
// (`EnumHandler.CaseConstruction.cs` tuple-element arm + the single
// bound-generic-payload arm) — unlike the public-type path, which excludes
// closure-bearing bound generics. Before this fixture, nothing anywhere in
// BindingTests exercised an enum-with-optional-closure payload, so whether the
// construction path emits compilable C# was unverified.
//
// Maximum-case shape on purpose: the closure takes arguments AND returns a
// non-Void value (`(Int32, String) -> Bool`), so the projected closure type
// is non-trivial rather than the degenerate `(() -> Void)?`.

/// Enum whose payload cases carry an `Optional<Closure>`, in both the
/// single-payload and tuple-element shapes.
public enum ClosureCarrier {
    /// Single bound-generic payload: `Swift.Optional<(Int32, String) -> Bool>`.
    /// Hits the single-payload bound-generic construction arm.
    case withHandler(((Int32, String) -> Bool)?)
    /// Tuple payload whose SECOND element is an `Optional<Closure>`. Hits the
    /// tuple-element construction arm (`proj is OptionalProjection`).
    case labeled(label: String, handler: ((Int32, String) -> Bool)?)
    /// No-payload case so the enum has a tag-only arm too.
    case none
}

/// Invokes the stored handler from `.withHandler`, returning the closure's
/// result, or `false` if the case has no handler or is a different case.
/// Lets a C# round-trip test construct `.withHandler` and observe dispatch.
public func invokeCarrierHandler(_ carrier: ClosureCarrier, code: Int32, message: String) -> Bool {
    switch carrier {
    case .withHandler(let handler):
        guard let handler = handler else { return false }
        return handler(code, message)
    case .labeled(_, let handler):
        guard let handler = handler else { return false }
        return handler(code, message)
    case .none:
        return false
    }
}

/// Returns the label from `.labeled`, or "<none>" otherwise.
public func carrierLabel(_ carrier: ClosureCarrier) -> String {
    switch carrier {
    case .labeled(let label, _): return label
    default: return "<none>"
    }
}
