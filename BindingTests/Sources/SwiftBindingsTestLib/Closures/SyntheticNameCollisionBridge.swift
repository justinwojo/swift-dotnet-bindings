// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Synthetic-name collision guard on the MethodClosureBridge @_cdecl wrapper: the wrapper
// hardcodes synthetic Swift identifiers — `self_` (the explicit self pointer param), `selfObj`
// (the self reconstruction local), and per-closure `cdecl` / `_box_{N}` locals. A user parameter
// spelled the same name would collide and produce an "invalid redeclaration" — and because the
// generator already returned exit 0, it would emit broken Swift that only fails much later, in
// `swiftc`. The guard (`ComputeSyntheticNames`) reserves every synthetic through a
// `SyntheticNameScope` seeded with the user identifiers in the wrapper's scope, renaming a
// colliding synthetic to a `__`-prefixed variant.
//
// Each method below puts a user parameter on one reserved synthetic name. The compile gate
// proves the generated Swift + C# compile (no redeclaration); the runtime test proves the
// completion still fires with the correct value — i.e. the renamed synthetic is used CONSISTENTLY
// across both emission paths (EmitSwiftWrapper and EmitSwiftMultiClosureWithPointerWrapping, which
// derive the mapping independently from the same pure function).
//
// Every method takes an `@escaping (Result<Int32, any Error>) -> Void` completion so it routes
// through MethodClosureBridge (the `any Error` existential activates MCB) and exercises the
// escaping box-wrapping path (where `_box_{N}` is emitted).
public final class SyntheticNameCollisionHost {
    public init() {}

    /// User param `self_` collides with the synthetic self-pointer param.
    public func runSelfCollision(self_: Int32, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(self_ + 1))
    }

    /// User param `selfObj` collides with the synthetic self-reconstruction local.
    public func runSelfObjCollision(selfObj: Int32, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(selfObj + 2))
    }

    /// User param `cdecl` collides with the synthetic per-closure func-ptr local.
    public func runCdeclCollision(cdecl: Int32, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(cdecl + 3))
    }

    /// User param `_box_0` collides with the synthetic escaping-closure box local for closure 0.
    public func runBoxCollision(_box_0: Int32, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(_box_0 + 4))
    }

    /// User param `__adapter0` collides with the synthetic per-closure adapter local that
    /// `EmitSwiftMultiClosureWithPointerWrapping` declares (`let __adapter{N}`). The `Result<…,
    /// any Error>` completion routes through that pointer-wrapping path, so this is the closure-0
    /// adapter name — distinct from the `_box_0` box local. The guard renames the synthetic so the
    /// adapter binding and its call-site reference stay consistent despite the colliding user param.
    public func runAdapterCollision(__adapter0: Int32, completion: @escaping (Result<Int32, any Error>) -> Void) {
        completion(.success(__adapter0 + 5))
    }
}
