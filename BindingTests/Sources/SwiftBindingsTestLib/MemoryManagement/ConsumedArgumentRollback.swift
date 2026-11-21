// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A consumed argument's +1 is rolled back when a later argument fails
//
// An initializer consumes its arguments, so the binding retains each one for the callee before the
// call. When a later argument cannot be converted (here, an `any Error` whose C# wrapper is already
// disposed), the call never happens and nothing consumes the retains taken for the earlier
// arguments: they have to be given back, or each earlier argument leaks.
//
// The initializers are failable inits of non-frozen structs, which keeps them on the direct
// Swift-convention call where the binding hands each argument over itself (a class's failable init
// goes through the C-convention wrapper, where Swift takes the retains). Every value here feeds the
// shared allocation counters, so a leaked +1 shows up as an instance that never deinitializes.

/// Takes two consumed errors.
public struct ConsumedErrorPair {
    public let total: Int32

    public init?(first: any Error, second: any Error) {
        let a = (first as? TrackedError)?.tag ?? 0
        let b = (second as? TrackedError)?.tag ?? 0
        if a < 0 || b < 0 { return nil }
        total = a + b
    }
}

/// Takes a consumed NSObject-rooted object, then a consumed error.
public struct ConsumedObjCAndError {
    public let total: Int32

    public init?(shape: TrackedObjCShapeThing, error: any Error) {
        let b = (error as? TrackedError)?.tag ?? 0
        if b < 0 { return nil }
        total = shape.tag + b
    }
}
