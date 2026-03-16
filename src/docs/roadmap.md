# Roadmap

**Updated**: March 16, 2026

---

## Generator Improvements

| Item | Impact | Effort | Notes |
|------|--------|--------|-------|
| Optional<Primitive/Enum> in closures | Various closure-accepting APIs | Medium | Different ABI from pointer-based Optional |
| Complex enums in closures | Various | Medium | Structural emitter change |
| AnyError -> Exception error handling | Ergonomics | Medium | `SwiftException : Exception` wrapping `AnyError` |
| Cross-module protocol conformances | Polymorphic use through cross-module interfaces | Medium | Thread conformance declarations across module boundaries |
| Architectural generic closures (~45 methods) | RxSwift subscribe/flatMap, Alamofire interceptors | Large | Deferred from foundation roadmap P3.5 |
| Protocol extension associated type context (GRDB) | GRDB wrapper compilation (666 errors contained) | Large | Protocol extension wrappers use bare associated type names (`Element`, `Base`) outside their protocol context. Needs full generic constraint context carried from protocol definition into wrapper signatures. Containment gate prevents emission today. See `Completed/swift-wrapper-errors.md` EC-17. |
| String enum raw values | GRDB ResultCode, CryptoSwift error codes | Blocked | No data source in compiled xcframeworks |
| `ConfigurationValue` property name collision | Nuke readability | Small | Alternative disambiguation strategy |
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque pass-through handles |
| Stripe validation config fixes | StripeCryptoOnramp, StripeIssuing | Small | StripeCryptoOnramp: add `StripeCameraCore` transitive dep to manifest. StripeIssuing: add `Stripe3DS2` as `--framework-dependency`. Config-only, no code changes. |

## NativeAOT Device Stability — Remaining Items

Target met (373 pass, 0 fail, 0 exit-crashes across 15 libraries). These items would increase per-library test coverage within already-passing libraries. See `Completed/nativeaot-stability-sessions.md` for full context.

| Item | Affects | Effort | Notes |
|------|---------|--------|-------|
| Foundation.Data projection (`DataProjection`) | Starscream, any library with `Data` params | Medium | Same pattern as `DateProjection`: pass `UnsafeRawPointer + nint` at @_cdecl boundary, reconstruct `Data(bytes:count:)` inside wrapper. Blocks `WebSocketEvent.Binary(byte[])` and `WebSocketEvent.Ping(Data?)`. |
| Struct singleton second-access crash | Alamofire `URLEncoding.Default` | Unknown | SIGBUS on second access. Copy via `initializeMemory` + destroy via `deinitialize` may corrupt ARC reference counts on repeated calls. Needs investigation. |
| Enum case dispose crash (cumulative) | Starscream `WebSocketEvent.ViabilityChanged` | Unknown | SIGSEGV after ~3 enum cases created and destroyed. May be related to struct singleton issue or GC-finalized enum heap corruption. |
| CallConvSwift `URL.init(string:)` wrapper | Starscream WebSocket construction | Small | `MarshalDirectiveException` on NativeAOT. Needs @_cdecl wrapper using `UnsafePointer<UInt8> + nint` (same pattern as existing string params). |

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
| Bulk retain/release helpers | Low-medium | Perf win for large collections. Deferred — do when relevant. |

SuppressGCTransition on ARC P/Invokes: complete (commit `865430cb`).

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| **ObjC binding integration** (replace Objective Sharpie) | Large (2-3 weeks) | `Future/objc-binding-integration.md` |
| **SPM package support** (source -> xcframework -> bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |

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

## Recently Completed

| Item | Completed | Notes |
|------|-----------|-------|
| C# keyword escaping in enum case labels | `51efaeec` (Mar 14) | FilterScope.swift fixture enabled |
| ObjC static method wrapper generation | `eb303ab4` (Mar 11) | Universal @_cdecl wrappers for static + instance methods |
| NativeAOT device stability target | Mar 15 | 373 pass, 0 fail, 14/15 libraries success. See `Completed/nativeaot-stability-sessions.md` |
| Ownership automation (3 sessions) | Feb | `Completed/ownership-automation-design.md` |
