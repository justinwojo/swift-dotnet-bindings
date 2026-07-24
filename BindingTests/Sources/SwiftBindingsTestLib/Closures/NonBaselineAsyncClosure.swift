// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Skip-class fixture for the non-baseline async-closure tombstone.
//
// Reproduces the rive-ios `RiveUIView.init(rive:...)` shape: a *sync* member
// (constructor or instance method) that takes an escaping async-throwing closure
// whose return type is a Swift class (non-blittable → NOT the baseline async
// bridge shape per `ClosureHandler.IsBaselineAsyncClosure`).
//
// Before the fix, three independently-maintained emitter stages disagreed on this
// shape: the marshal-plan builder gated the closure `Box` local on the broad
// `!IsAsyncClosure`, the body-emission router only sent baseline async closures to
// the self-contained async setup (everything else fell through to the legacy sync
// escaping path, which referenced an undeclared `{name}Box` + a never-emitted
// trampoline), and the P/Invoke emitter downgraded the parameter to the
// `Swift.AnyType` placeholder while the call still passed the raw managed delegate.
// The net result was a member body with CS0103 (undeclared box / trampoline) and
// CS1503 (Func<Task<Class>> → Swift.AnyType).
//
// With the fix, a non-baseline async closure is classified `!IsSupportedClosure`,
// so the whole member routes to the SB0005 closure tombstone: it stays visible at
// the C# surface (the closure parameter projects to `object?`, the member carries
// `[Obsolete(... DiagnosticId="SB0005")]` + `[UnsupportedSwiftType(...)]`) but its
// body throws `NotSupportedException`.

import Foundation

/// Swift class used as the non-baseline async closure's return type. A class return
/// is non-blittable, so `() async throws -> AsyncFactoryPayload` is NOT a baseline
/// async closure.
public final class AsyncFactoryPayload {
    public let tag: Int32
    public init(tag: Int32) { self.tag = tag }
}

/// Sync constructor taking a non-baseline async-throwing closure — the exact
/// rive-ios `init(rive:)` shape. Must emit the SB0005 tombstone, NOT a broken body.
public final class NonBaselineAsyncClosureFactory {
    public init(factory: @escaping () async throws -> AsyncFactoryPayload) {
        // body unused — the generator never calls this from C#.
        _ = factory
    }
}

/// Sync instance method taking the same non-baseline async-throwing closure shape.
/// Method variant of the constructor case above.
public final class NonBaselineAsyncClosureConsumer {
    public init() {}

    public func configure(factory: @escaping () async throws -> AsyncFactoryPayload) {
        _ = factory
    }
}
