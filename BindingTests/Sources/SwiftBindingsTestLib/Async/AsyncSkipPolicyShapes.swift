// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async + skip-policy shapes (direct-CallConvSwift PInvoke for skipped wrapper)
//
// These shapes pin the skip behaviour added by `WrapperValidation.IsSkippedWrapperDirectPInvoke`.
// In xcframework mode, async methods that DON'T receive a @_cdecl wrapper but carry one of:
//   1) a method-own generic parameter (Swift `some Protocol` or explicit `<T>`),
//   2) a top-level existential parameter (`any Protocol`),
//   3) a closure parameter without a closure-cdecl wrapper,
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

/// Shape (3): async + closure parameter without a closure-cdecl wrapper. The closure's
/// destroy thunk + ownership transfer can only be plumbed through the cdecl-wrapped path.
public class AsyncSkipPolicyClosure {
    public init() {}

    public func runAsync(_ work: @escaping (Int32) -> Int32) async -> Int32 {
        return work(7)
    }
}
