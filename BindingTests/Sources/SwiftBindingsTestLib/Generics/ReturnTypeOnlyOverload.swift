// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Return-type-only overload disambiguation
//
// Regression coverage for AppIntents 0.12.0 site #2 (doc 14 carry-out):
// `AppShortcutsBuilder.buildExpression(_:)` ships two overloads with the
// same parameter type but different return types:
//
//   @_disfavoredOverload
//   public static func buildExpression(_ component: AppShortcut) -> AppShortcut
//   public static func buildExpression(_ component: AppShortcut) -> [AppShortcut]
//
// The wrapper for the disfavored overload calls
// `AppShortcutsBuilder.buildExpression(componentVal)` then writes the
// result through `resultPtr.initializeMemory(as: AppShortcut.self, ...)`.
// Without disambiguation, Swift's overload resolution at the `let result =`
// line picks the non-disfavored overload (returning `[AppShortcut]`), and
// the `initializeMemory(as:)` call rejects the type mismatch.
//
// The fix in `MethodWrapperEmitter.HasReturnTypeOnlyOverloadSibling` detects
// the same-name same-param-types different-return collision and forces the
// call expression through a function-reference `as` cast pinned to this
// overload's exact signature.

public struct ReturnTypeOnlyOverloadHost {
    @_disfavoredOverload
    public static func selectExpression(_ value: VariadicSection) -> VariadicSection {
        return value
    }
}

extension ReturnTypeOnlyOverloadHost {
    public static func selectExpression(_ value: VariadicSection) -> [VariadicSection] {
        return [value]
    }
}
