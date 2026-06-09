// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Nested-of-parent generic-host constructor coverage
//
// Regression coverage for the AppIntents 0.12.0 `EnumURLRepresentation<TEnum>.
// StringInterpolation` shape: a generic host whose constructor accepts a nested
// struct parameterised on the host's own generic. Before the fix, this shape
// fell through to the [Obsolete(SB0001)] direct-`CallConvSwift` fallback, with
// the same heap-corruption risk as the KeyPath shape.
//
// `IsNestedTypeOfParentGeneric` widens the static-factory gate so the GSF
// shim emits a normalized `(resultPtr, arg: UnsafeRawPointer, T_metadata)`
// @_cdecl wrapper, eliminating the SB0001 fallback at this site. The default
// `assumingMemoryBound(to: Outer<T>.Inner.self).pointee` reconstruction in
// `EmitGenericStaticFactoryConstructor` handles the value-type Inner — no
// branch is required.

public struct NestedHostStruct<TElement> {
    public struct Caption {
        public let text: String
        public init(text: String) { self.text = text }
    }

    public let caption: Caption
    public init(caption: Caption) { self.caption = caption }
    public var captionText: String { caption.text }
}

public class NestedHostClass<TElement> {
    public struct Tag {
        public let label: String
        public init(label: String) { self.label = label }
    }

    public let tag: Tag
    public init(tag: Tag) { self.tag = tag }
    public var tagLabel: String { tag.label }
}

// MARK: - Cross-host nested-of-parent (param outer != host name)
//
// Mirrors the AppIntents 0.12 site `EnumSingleURLRepresentation.init(
// stringInterpolation: EnumURLRepresentation<TEnum>.StringInterpolation)`,
// where the parameter's outer NamedTypeSpec is a DIFFERENT generic type than
// the host. The predicate (`IsNestedTypeOfParentGeneric`) REJECTS this shape
// because cross-host stamping through `initializeMemory(as: Self.self, …)`
// faults on the host's value-witness destroy at Dispose — verified by the
// `TestCrossHost*` runtime fixtures (currently `[Skip]`'d as durable
// regression markers; see doc 13 site #1 follow-on for the runtime
// diagnosis and future fix path). The fixtures remain in-tree so when the
// runtime fault is fixed, the predicate widens, the `[Skip]` drops, and
// these tests turn green as the durable gate.

public struct CrossHostOuter<T> {
    public struct Body {
        public let text: String
        public init(text: String) { self.text = text }
    }
}

public struct CrossHostSiblingStruct<T> {
    public let payload: CrossHostOuter<T>.Body
    public init(by payload: CrossHostOuter<T>.Body) { self.payload = payload }
    public var payloadText: String { payload.text }
}

public class CrossHostSiblingClass<T> {
    public let payload: CrossHostOuter<T>.Body
    public init(by payload: CrossHostOuter<T>.Body) { self.payload = payload }
    public var payloadText: String { payload.text }
}
