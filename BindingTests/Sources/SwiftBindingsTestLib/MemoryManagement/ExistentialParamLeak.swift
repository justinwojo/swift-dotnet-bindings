// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential-PARAMETER ARC leak fixtures (audit P1-03)
//
// The mirror image of ExistentialReturnLeak.swift: a value-type conformer passed
// from C# INTO a Swift `any P` parameter. The generated C# wrapper calls
// `ExistentialContainerFactory.GetOrCreate(value)`, which for a boxable value
// conformer freshly boxes the payload at +1 (an inline `InitializeWithCopy` for a
// small conformer, or a `swift_allocBox` for one that overflows the 3-word inline
// buffer). The Swift parameter is `@in_guaranteed` (borrowed — the callee does NOT
// release), so the C# caller owns that +1 and must run the existential
// value-witness destroy after the call returns. Before the P1-03 fix nothing
// released it, leaking every embedded `TrackedRef` per call.
//
// These functions only READ the existential (so they never alter ownership) and
// return a scalar, keeping the C# wrapper on the existential-parameter marshalling
// path without dragging in unrelated return-marshalling. The probes embed
// `TrackedRef`s (declared in MemoryManagement/LeakDetection.swift) and structure
// the leak around a surviving C# owner: a leaked per-call box retain pins the refs
// alive even after the owner is disposed (live count never returns to 0).

/// Small value-type conformer (`Renderable`) whose two stored `TrackedRef`s fit
/// within the opaque existential's 3-word inline payload buffer, so `GetOrCreate`
/// stores it INLINE (no `swift_allocBox`). Reading it as `any Renderable` boxes a
/// fresh inline copy at +1 (an `InitializeWithCopy` retaining both refs); the
/// existential value-witness destroy of the inline container must release them.
public struct InlineTrackedRenderable: Renderable {
    public let a: TrackedRef
    public let b: TrackedRef

    public init(tag: Int32) {
        self.a = TrackedRef(tag: tag)
        self.b = TrackedRef(tag: tag)
    }

    public func render() -> String { "InlineTrackedRenderable(\(a.tag))" }
}

/// Reads an `any Renderable` parameter and returns the rendered string's length.
/// Drives the synchronous `@_cdecl` existential-parameter marshalling path
/// (`WrapperEmitter.Marshalling.cs` `GetOrCreate(..., out owns)` + the finally's
/// conditional existential destroy). A boxable value conformer is boxed at +1 here;
/// a class/proxy conformer is borrowed (+0) and must NOT be destroyed.
public func consumeRenderable(_ r: any Renderable) -> Int32 {
    return Int32(r.render().count)
}

/// Asynchronous sibling of `consumeRenderable`. The async wrapper has no foreground
/// `finally`, so the freshly boxed +1 is balanced by the async-callback cleanup loop
/// (`ExistentialContainerHeap` carrying the owns-bit + witness count) after the Swift
/// continuation has finished reading the `@in_guaranteed` buffer.
public func consumeRenderableAsync(_ r: any Renderable) async -> Int32 {
    await Task.yield()
    return Int32(r.render().count)
}
