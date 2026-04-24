// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// SKIP: Cannot reproduce direct-AnyType enum payload in BindingTests.
//
// The skip-gates in EnumHandler.CaseInspection.cs (lines 138 and 312) suppress
// TryGet emission when an enum case's resolved C# payload type contains
// "Swift.AnyType". In real-world libraries (e.g. Lottie's
// `(nint, AnyType)` pattern), this happens because the ABI parser cannot fully
// resolve a referenced Swift type — the TypeDatabase falls through to AnyType.
//
// The unit tests `Emit_EnumCaseWithDirectAnyTypePayload_SkipsTryGetMethod` and
// `Emit_EnumCaseWithTupleContainingAnyType_SkipsTryGetMethod` reproduce both
// gates by registering a stub module with no types and pointing a NamedTypeSpec
// at it (`Lottie.UnknownType`).
//
// In BindingTests, every type in `SwiftBindingsTestLib` and
// `SwiftBindingsTestLibDependency` IS registered (the generator consumes both
// modules' xcframeworks via `--xcframework` and `--framework-dependency`), and
// every Apple framework type the bindings can reference is auto-bridged via
// `apple-frameworks.json`. To force a direct AnyType, the payload would have to
// reference a Swift type that:
//   1. Is publicly visible (so Swift's access checker accepts it in a public
//      enum case payload), AND
//   2. Fails to resolve in the generator's TypeDatabase at codegen time.
//
// Tried / ruled out:
//   - Cross-module reference to `SwiftBindingsTestLibDependency` types: the
//     dependency xcframework is wired in, so these resolve to their concrete
//     C# types (no AnyType).
//   - `Any` / unconstrained-protocol existential payload: routes through the
//     existential code path (returns `ExistentialContainer0`), not the
//     fallback path that produces "Swift.AnyType".
//   - Internal/private type as payload: rejected by the Swift compiler — a
//     public enum case cannot expose an internal type.
//   - Apple-framework type from a less-common framework: auto-bridged via
//     `apple-frameworks.json`, so still resolves.
//
// Coverage decision: rely on the unit tests for the gate. They directly assert
// the absence of the dangerous emitted code (TryGet method and
// `out Swift.AnyType value` parameter) and confirmed they catch a reverted
// gate. A runtime repro would only add value if it triggered a real Swift
// codegen path that the unit tests can't simulate — and the unit fixture
// (an unregistered type in a registered module) is already an exact
// reproduction of the Lottie scenario at the TypeDatabase level.
