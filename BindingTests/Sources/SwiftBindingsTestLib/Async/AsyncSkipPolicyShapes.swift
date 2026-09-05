// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async + skip-policy shapes (direct-CallConvSwift PInvoke for skipped wrapper)
//
// These shapes pin the skip behaviour added by `WrapperValidation.IsSkippedWrapperDirectPInvoke`.
// In xcframework mode, async methods that DON'T receive a @_cdecl wrapper but carry one of:
//   1) a method-own generic parameter (Swift `some Protocol` or explicit `<T>`),
//   2) a top-level existential parameter (`any Protocol`),
//   3) a closure parameter with no carrier the cdecl wrapper can render,
// would, before the fix, fall through to a direct CallConvSwift @_silgen_name trampoline.
// The Swift async ABI's continuation-passing layout is under-specified for that path,
// so the safe outcome is to emit a `// Unsupported: ... ABI-unsafe` comment instead of
// a runtime-crashing P/Invoke. The skip-surface baseline records the expected markers.

public protocol SkipPolicyValidator {
    func validate(_ value: Int32) -> Bool
}

public struct DefaultSkipPolicyValidator: SkipPolicyValidator {
    public init() {}
    public func validate(_ value: Int32) -> Bool { return value > 0 }
}

/// Shape (1): async + method-own generic. Mirrors StoreKit's
/// `Product.purchase(confirmIn: some UIScene)` — `some Protocol` becomes a
/// method-level generic parameter that the legacy direct path can't plumb.
public class AsyncSkipPolicyMethodGeneric {
    public init() {}

    public func purchaseAsync<S: SkipPolicyValidator>(confirmIn validator: S) async -> Int32 {
        return validator.validate(1) ? 1 : 0
    }
}

/// Shape (2): async + top-level existential parameter. The PWT / metadata for `any Protocol`
/// can't be threaded through the legacy CallConvSwift async trampoline.
public class AsyncSkipPolicyExistential {
    public init() {}

    public func validateAsync(using validator: any SkipPolicyValidator) async -> Bool {
        return validator.validate(42)
    }
}

/// Shape (3): async + closure parameter whose ownership the cdecl wrapper can carry. The
/// closure's destroy thunk + ownership transfer can only be plumbed through the cdecl-wrapped
/// path, and an `@escaping` closure now IS plumbed through it: it rides the same
/// (funcPtr, context) pair and Swift-ARC owner token as an Optional callback, so this member
/// binds rather than skipping. It stays here as the negative control for shape (3b) below —
/// the two differ only in escaping-ness, which is exactly the discriminator.
public class AsyncSkipPolicyClosure {
    public init() {}

    public func runAsync(_ work: @escaping (Int32) -> Int32) async -> Int32 {
        return work(7)
    }
}

/// Shape (3b): async + NON-escaping closure — the arm of shape (3) that still has no carrier.
/// The owner token that keeps the managed delegate alive past the `@_cdecl` return is only
/// sound for an effectively-escaping closure; a non-escaping one is freed by the C# wrapper's
/// `finally` as soon as the call returns, so promoting it would hand the async body a freed
/// delegate. It falls through to the legacy direct path and is skipped as ABI-unsafe.
public class AsyncSkipPolicyNonEscapingClosure {
    public init() {}

    public func runAsync(_ work: (Int32) -> Int32) async -> Int32 {
        return work(7)
    }
}
