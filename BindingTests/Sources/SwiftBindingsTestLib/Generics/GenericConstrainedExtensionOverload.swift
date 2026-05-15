// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - GenericConstrainedExtensionOverload — ObjectMapper regression
//
// Reproduces ObjectMapper's `Mapper<N>` shape that broke at 0.11.0: a generic
// class declares a sibling method in a `where N : Narrower` extension. The GSM
// emitter previously produced a wrapper extension for that method WITHOUT the
// `where N : Narrower` clause, so at the wrapper's call site the constrained
// extension method was either invisible (forcing overload misresolution onto
// an unconstrained body sibling) or its body's `obj.method(...)` failed to
// typecheck because `N` lacks the narrower conformance.
//
// Real-world signal: ObjectMapper's `extension Mapper where N : ImmutableMappable`
// added a `map(JSONObject: Any) throws -> N` sibling to the body's
// `func map(JSONObject: Any?) -> N?`. swiftc emitted:
//   error: value of optional type 'N?' must be unwrapped to a value of type 'N'
// on the generated wrapper for the constrained method.
//
// The fix skips GSM emission for methods whose generic conformances narrow
// their parent's (conditional-conformance wrapper extensions are not yet
// supported). The skip emits a comment in the generated `.swift` so the
// symbol contract records the omission. The unconstrained body sibling
// continues to be emitted and round-trips from C#.
//
// The body method takes `JSONObject: Any?` matching ObjectMapper exactly, so
// the constrained sibling's overload-resolution shadow path (the original
// swiftc rejection) reproduces if the wrapper-skip regresses. This also
// exercises the Optional<Any> @_cdecl boundary: C# passes a buffer pointer
// to a `SwiftOptional<ExistentialContainer0>` (4-word EC: 3 payload words +
// 1 metadata pointer; nil via null-metadata extra-inhabitant) and the Swift
// wrapper reads it as `Optional<Any>` via `load(as:)`. The bare-Any C#
// projection currently boxes value types (bool/int/double/string) — a String
// payload is a Swift String value (not an object reference), which is exactly
// the shape that crashed under the prior single-AnyObject-pointer reading.

public protocol GenericConstrainedBaseLabel {
    var label: String { get }
}

public protocol GenericConstrainedImmutableLabel: GenericConstrainedBaseLabel {}

public struct GenericConstrainedTag: GenericConstrainedImmutableLabel {
    public let label: String
    public init(label: String) { self.label = label }
}

public final class GenericConstrainedExtensionMapper<N: GenericConstrainedBaseLabel> {
    private let stored: N?
    public init(stored: N?) { self.stored = stored }

    // Class-body method: parent's conformance only. Predicate does NOT fire
    // — wrapper emitted, callable from C#. Matches ObjectMapper's body
    // signature exactly so overload resolution against the constrained
    // sibling reproduces the original wrapper-compile failure if the skip
    // regresses.
    public func map(JSONObject: Any?) -> N? {
        return JSONObject == nil ? nil : stored
    }
}

// Constrained extension: narrows N to GenericConstrainedImmutableLabel.
// Predicate FIRES on this method — wrapper SKIPPED. Param/return mirror the
// ObjectMapper shape so the structural source pattern is preserved even though
// no @_cdecl wrapper is emitted.
extension GenericConstrainedExtensionMapper where N: GenericConstrainedImmutableLabel {
    public func map(JSONObject: Any) throws -> N {
        guard let stored else {
            throw GenericConstrainedExtensionMapperError.missing
        }
        _ = JSONObject
        return stored
    }
}

public enum GenericConstrainedExtensionMapperError: Error {
    case missing
}

// Factory: returns an instance bound to a concrete N that satisfies the
// narrower constraint. C# tests call through this to construct the generic
// without needing to name N on the binding side.
public func makeGenericConstrainedExtensionMapper(
    label: String?
) -> GenericConstrainedExtensionMapper<GenericConstrainedTag> {
    if let label {
        return GenericConstrainedExtensionMapper<GenericConstrainedTag>(
            stored: GenericConstrainedTag(label: label))
    }
    return GenericConstrainedExtensionMapper<GenericConstrainedTag>(stored: nil)
}
