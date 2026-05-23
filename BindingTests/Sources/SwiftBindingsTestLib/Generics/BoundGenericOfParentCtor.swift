// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Bound-generic-of-parent constructor coverage
//
// Regression coverage for the AppIntents 0.12.0 site #4 shape:
// `IntentParameterSummary<Intent>.init(_: ParameterSummaryString<Intent>, ...)`.
// The constructor parameter is a foreign generic struct parameterised on the
// host's own generic — same shape as `AliasGenericPayload<T>` used as a param
// to a generic host. Before doc 14 the constructor's `CanEmitStaticDispatch`
// admission gate rejected this shape (only bare T / Array<T> / KeyPath /
// nested-of-parent were admitted). The widened gate routes the wrapper through
// the existing static-factory pattern with the default
// `assumingMemoryBound(to: Box<T>.self).pointee` reconstruction.

public struct BoxedGenericPayload<TElement> {
    public let stored: TElement
    public init(stored: TElement) { self.stored = stored }
}

public struct BoundGenericOfParentHost<TElement> {
    public let box: BoxedGenericPayload<TElement>
    public init(boxed box: BoxedGenericPayload<TElement>) {
        self.box = box
    }
    public var storedDescription: String { "\(box.stored)" }
}
