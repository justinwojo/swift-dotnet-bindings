// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Static method returning `(any Error)?`
//
// Regression for the PInvoke-vs-body sret-shape drift bug
// (uninitialized sret buffer for optional-error return). Mirrors
// Nuke 13.0.5's `static func validate(response:) -> (any Error)?` shape:
//
//   - direct CallConvSwift (no @_cdecl wrapper, no native thunk, no
//     wrapper-library indirection)
//   - synchronous
//   - returns `Optional<any Error>` — address-only on every Apple ABI Swift
//     supports, so the Swift caller hands an `@out` (sret) buffer in the
//     hidden `x8` register and the function writes the optional payload there
//
// Pre-fix, the generator emitted a wrapper that allocated the sret buffer +
// constructed `swiftIndirectResult` but the matching PInvoke signature
// returned `IntPtr` with no sret slot. The .NET caller dropped the returned
// IntPtr and unmarshalled `SwiftOptional<ExistentialContainer1>` from the
// freshly-allocated, never-written buffer — fabricating an `AnyError` over
// uninitialized memory.

public final class StaticOptionalErrorReturn {
    public init() {}

    /// Returns `nil` (validation passed). Direct CallConvSwift sret —
    /// the optional's `none` tag must round-trip to C# as `null`.
    public static func validateNone() -> (any Error)? {
        return nil
    }

    /// Returns a concrete Swift error existential. Pre-fix this fabricated
    /// an `AnyError` over uninitialized memory; post-fix it materializes the
    /// real `MathError.divisionByZero` payload.
    public static func validateMathError() -> (any Error)? {
        return MathError.divisionByZero
    }

    /// Returns an enum-with-associated-value error. Exercises the inner
    /// existential's witness-table dispatch through the indirect-result buffer
    /// (the `tooLong(maxLength:)` payload is part of the existential's
    /// `extraInhabitants`-stored payload word).
    public static func validateValidationError() -> (any Error)? {
        return ValidationError.tooLong(maxLength: 7)
    }

    /// Returns an NSError-bridged existential. Verifies the ObjC-bridge path
    /// through the same sret buffer — the inner reference must survive the
    /// indirect-result extraction without being freed early.
    public static func validateNSError() -> (any Error)? {
        return NSError(
            domain: "StaticValidate",
            code: 99,
            userInfo: [NSLocalizedDescriptionKey: "Static validation failure"]
        )
    }
}
