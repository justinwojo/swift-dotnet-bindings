# Roadmap

**Updated**: March 10, 2026

---

## Generator Improvements

| Item | Impact | Effort | Notes |
|------|--------|--------|-------|
| C# keyword escaping in enum case labels (S1) | Alamofire `RequestModifier`, any enum with `in:`/`for:`/`operator:` labels | Small | Generator produces `__@in` (invalid) instead of `@__in`. Fix in `StripVerbatimPrefix` or compound variable construction. **Test fixture staged**: `TestFramework/.../EdgeCases/FilterScope.swift.disabled` — rename to `.swift` to enable after fix. |
| ObjC static method wrapper generation | ObjC class static methods (e.g. Stripe `STPImageLibrary.brandImage(for:)`) | Medium | P/Invoke targets mangled Swift symbols that don't exist in wrapper xcframework. Need `@_cdecl` trampolines for static methods on `IsObjCRooted` classes. Infrastructure exists for constructors/closures/optional pointers. |
| Optional<Primitive/Enum> in closures | Various closure-accepting APIs | Medium | Different ABI from pointer-based Optional |
| Complex enums in closures | Various | Medium | Structural emitter change |
| AnyError -> Exception error handling | Ergonomics | Medium | `SwiftException : Exception` wrapping `AnyError` |
| Cross-module protocol conformances | Polymorphic use through cross-module interfaces | Medium | Thread conformance declarations across module boundaries |
| Architectural generic closures (~45 methods) | RxSwift subscribe/flatMap, Alamofire interceptors | Large | Deferred from foundation roadmap P3.5 |
| String enum raw values | GRDB ResultCode, CryptoSwift error codes | Blocked | No data source in compiled xcframeworks |
| `ConfigurationValue` property name collision | Nuke readability | Small | Alternative disambiguation strategy |
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque pass-through handles |

## SwiftUI Bridge (4 remaining sessions)

Active roadmap: `swiftui-roadmap.md`

| Session | Focus | Priority |
|---------|-------|----------|
| **1B** | Closure non-primitive returns (String, class) | Medium |
| **4B** | Constrained generics (`<T: Identifiable>`, `<T: Hashable>`) | Medium |
| **5** | Lifecycle (`onAppear`/`onDisappear`), presentation helpers | Medium-low |
| **6** | Observable binding (C# -> Swift reactivity), corpus tracking | Low |

Sessions 1A-3 + 4A + 4C already cover the vast majority of real-world SwiftUI views.

## Runtime

See `swift-runtime-improvements.md` for details.

| Item | Effort | Notes |
|------|--------|-------|
| Bulk retain/release helpers | Low-medium | Perf win for large collections |

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| **ObjC binding integration** (replace Objective Sharpie) | Large (2-3 weeks) | `Future/objc-binding-integration.md` |
| **SPM package support** (source -> xcframework -> bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Emitter architecture redesign** | Very large | `Completed/emitter-redesign-proposal.md` |

---

## Contributor Onboarding

| Item | Effort | Notes |
|------|--------|-------|
| `CONTRIBUTING.md` | 0.5 session | Architecture overview, issue/PR templates. Currently excellent AI docs but nothing for human contributors. |

## Upstream .NET Runtime Issues

NativeAOT resolves most Mono JIT issues. Device builds are unaffected. See `known-issues-workarounds.md` for full details. Drafts in `Future/upstream-bug-reports-draft.md` and `Future/upstream-nativeaot-simulator-issue.md`.

| Issue | Affects | Status |
|-------|---------|--------|
| JIT assertion crash (CallConvSwift) | Simulator (Mono) | Draft ready — needs filing |
| Non-blittable types with CallConvSwift | Simulator (Mono) | Draft ready — needs filing |
| SafeHandle in async P/Invoke | All runtimes | Draft ready — needs filing |
| NativeAOT on iossimulator-arm64 | Simulator | Draft ready — needs filing |

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Ownership semantics (`consume`/`borrow`) | Swift 6 feature with unclear ABI impact |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |
