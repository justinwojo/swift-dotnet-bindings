// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Variadic-pack result-builder coverage
//
// Regression coverage for AppIntents 0.12.0 sites #5 and #6:
// - `AppShortcutsBuilder.buildBlock(_ components: AppShortcut...) -> [AppShortcut]`
// - `AppShortcutsBuilder.buildBlock(_ components: [AppShortcut]...) -> [AppShortcut]`
//
// Before doc 14 these signatures fell through to the [Obsolete(SB0001)]
// CallConvSwift fallback because `MethodWrapperEmitter` hard-rejected any
// method with a variadic parameter. Swift's `T...` lowers to `Array<T>` at
// SIL — the ABI is identical — but `(T...) -> R` and `([T]) -> R` are
// distinct types at the type-checker layer and Swift has no runtime splat
// operator (`foo(myArray)` where foo takes `T...` doesn't compile). The
// `unsafeBitCast` bridge in `MethodWrapperEmitter` casts the function
// reference from variadic to array form, then calls with the runtime array.

// Non-frozen struct → C# projects as a class with SafeHandle
// (`ClassWithOpaquePayload`), which composes cleanly with `SwiftArray<T>`
// at the wrapper-helper boundary. The frozen-with-memory shape
// (`ClassWithBufferStruct`) is incompatible with `SwiftArray<T>` element
// passing today — see `FrozenWithMemoryProjection.GetParameterElementConversion`
// (returns null on purpose) — and is a separate generator limitation
// tracked outside doc 14. Mirrors the way `AppShortcut` projects
// (non-frozen struct conforming to `AppIntent`).
public struct VariadicSection {
    public let title: String
    public init(title: String) { self.title = title }
}

/// Mirrors `AppShortcutsBuilder` shape without the `@resultBuilder` attribute
/// (the wrapper emission gates don't care about that attribute — only about
/// the signature shape).
public struct VariadicSectionBuilder {
    /// Site #5 shape: `(T...) -> [T]`.
    public static func buildBlock(_ components: VariadicSection...) -> [VariadicSection] {
        return components
    }

    /// Site #6 shape: `([T]...) -> [T]`. Flattens one level of arrays into a single array.
    public static func buildBlock(_ components: [VariadicSection]...) -> [VariadicSection] {
        return components.flatMap { $0 }
    }

    /// Empty-block overload that result-builder DSLs include for the zero-children path.
    /// Distinguishes the variadic overload from this no-arg overload at the wrapper's
    /// `unsafeBitCast(... as (T...) -> R, ...)` site (the type annotation forces the
    /// variadic-shaped overload).
    public static func buildBlock() -> [VariadicSection] {
        return []
    }
}
