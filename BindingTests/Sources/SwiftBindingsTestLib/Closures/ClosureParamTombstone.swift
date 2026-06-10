// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Skip-class fixture for Layer A closure-param tombstone (Fix K).
//
// Each member here has an unsupported closure parameter shape (closure-taking
// closure, which falls through `ClosureHandler.IsSupportedClosureParameterType`).
// We deliberately gate every member so `NestedClosureBridge.IsEligible` rejects:
//   - constructors          (rejected at IsConstructor check)
//   - throwing methods      (rejected at Throws check)
//   - throwing free function (rejected at Throws check)
//
// Without the Layer A tombstone, every member here would be dropped wholesale
// from the generated C# surface. With Layer A, each member is emitted as
// `[Obsolete(... DiagnosticId="SB0005")]` + `[UnsupportedSwiftType(...)]` with
// the closure parameter projected to `object?` and the body throwing
// `NotSupportedException`.
//
// Reproduces the API-vanish shape where members with unsupported closure-of-closure
// parameters were dropped wholesale from the generated C# surface.

import Foundation

/// Class with an init taking an unsupported closure-of-closure parameter.
public final class TombstoneDataLoader {
    public init(transform: @escaping (@escaping () -> Void) -> Void) {
        // body unused — generator never calls this from C#.
        _ = transform
    }
}

/// Class with throwing instance + static methods taking unsupported closure-of-closure parameters.
public final class TombstoneDecoderRegistry {
    public init() {}

    public func register(name: String,
                         decoder: @escaping (@escaping () -> Void) -> Void) throws {
        _ = name
        _ = decoder
    }

    public static func registerStatic(name: String,
                                      decoder: @escaping (@escaping () -> Void) -> Void) throws {
        _ = name
        _ = decoder
    }
}

/// Throwing free function taking an unsupported closure-of-closure parameter.
public func tombstoneMakeImageDecoder(
    factory: @escaping (@escaping () -> Void) -> Void
) throws {
    _ = factory
}

/// Class with two overloaded throwing methods that differ ONLY in the unsupported
/// closure shape. Every shape projects to `object?` in the tombstone signature,
/// so without dedup-key normalization the projected key would be distinct
/// (different delegate types) but the emitted C# signature would collide
/// (CS0111). Exercises the closure-tombstone-aware path in
/// `GetProjectedCSharpMethodKey` (IHandler.cs).
public final class TombstoneOverloadCollision {
    public init() {}

    public func handle(
        callback: @escaping (@escaping () -> Void) -> Void
    ) throws {
        _ = callback
    }

    public func handle(
        callback: @escaping (@escaping (Int) -> Void) -> Void
    ) throws {
        _ = callback
    }
}
