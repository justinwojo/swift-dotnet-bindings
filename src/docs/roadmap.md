# Roadmap

**Updated**: March 17, 2026

---

## Coverage Baseline (March 16, 2026)

Measured across 90 validation targets (56 libraries). This is the starting point — each session below projects its impact against these numbers.

| Metric | Count | % |
|--------|------:|--:|
| Total Swift types | 2,584 | |
| Types emitted | 2,174 | 84% |
| Total Swift members | 14,109 | |
| Members emitted (usable) | 9,521 | 67% |
| Members skipped | 4,588 | 33% |

Of the 9,521 emitted members:
- **6,320 (66%)** use safe `@_cdecl` wrappers (works on all runtimes)
- **2,691 (28%)** use `CallConvSwift` (SB0001 — NativeAOT only, Mono JIT crashes)
- **549 (6%)** are `SB0003` stubs (protocol members that throw `NotSupportedException` at runtime)

**Projected cumulative coverage after sessions 1–4: 67% → ~80%** (~1,900 members recovered)

---

## Prioritized Sessions

### ~~Session 0: TestFramework Generator Bug Fixes~~ (Complete)

Completed March 17, 2026. All 4 generator bugs fixed, Swift source restored, C# tests written. 90/90 validation, 480 runtime tests pass (477→480).

---

### Session 1: Struct & Closure Boundary Expansion

**Coverage impact**: ~480 skips recovered → 67% → ~70%
**Libraries affected**: 33+

The core blocker is that non-primitive frozen structs can't cross the `@_cdecl` wrapper boundary today. The approach (`UnsafeRawPointer` + `pointee`) already exists in the codebase for indirect returns. Once struct params work, several closure sub-shapes are directly unlocked, and Foundation.Data uses the identical pattern.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **Non-primitive frozen struct params** | 288 (27 libs) | Medium | Core pattern: pass structs via `UnsafeRawPointer` in `@_cdecl` wrapper, reconstruct via `.load(as: T.self)`. Affects NVActivityIndicatorView (56), Alamofire (33), GRDB (33), CryptoSwift (28), Lottie (24), Nuke (19), Kingfisher (15). |
| **Foundation.Data projection** | — | Medium | Same `UnsafeRawPointer + nint` pattern as `DateProjection`. Blocks `WebSocketEvent.Binary(byte[])` and `WebSocketEvent.Ping(Data?)` in Starscream. |
| **Closures with frozen struct params** | subset of 333 | Medium | Directly unlocked by struct param work. e.g., `(CGRect) -> Void`, `(LottieColor) -> Void`. |
| **Optional\<Primitive/Enum\> in closures** | subset of 333 | Medium | Different ABI from pointer-based Optional. Affects various closure-accepting APIs. |
| **Complex enums in closures** | subset of 333 | Medium | Structural emitter change for enum payloads in closure params. |

**Example APIs unlocked:**
- Methods taking `LottieColor` as param (Lottie)
- Methods taking config structs (Alamofire `URLRequest` wrappers)
- Methods taking crypto operation structs (CryptoSwift)
- `WebSocketEvent.Binary(byte[])` (Starscream)
- Callbacks with `CGRect`, enum, and optional primitive params across 33 libraries

---

### Session 2: Actor Isolation

**Coverage impact**: ~852 skips recovered → ~70% → ~76%
**Libraries affected**: 21

The single largest gap and growing as libraries adopt Swift concurrency. Every UIKit-facing property/method on modern Swift view classes gets `@MainActor` isolation. This session would design and implement async wrapper trampolines for isolated members.

| Sub-task | Effort | Notes |
|----------|--------|-------|
| **@MainActor wrapper trampoline design** | Medium | Emit `@MainActor`-annotated `@_cdecl` wrapper functions that dispatch to the isolated member |
| **Async P/Invoke integration** | Medium | C# side needs to call these via async interop, respecting SafeHandle lifetime constraints |
| **Custom actor support** | Medium | Extend beyond `@MainActor` to library-defined actors (lower frequency) |

**Approach** — emit `@MainActor`-annotated async wrappers:
```swift
@MainActor
@_cdecl("SBW_MyView_title_Get")
func wrapper_title_Get(_ self: UnsafeMutableRawPointer) async -> SBW_Utf8Slice {
    let obj = Unmanaged<MyView>.fromOpaque(self).takeUnretainedValue()
    return obj.title.toUtf8Slice()
}
```

**Affected libraries:** Parchment (160), Lottie (103), StripePaymentsUI (96), Kingfisher (66), PhoneNumberKit (63), FSPagerView (59), AMPopTip (58), Mappedin (40)

**Example APIs unlocked:**
- All properties/methods on `PagingViewController` (Parchment)
- All properties on `LottieAnimationView` (Lottie)
- All UI configuration properties on `NVActivityIndicatorView`
- Essentially all UIKit-facing view class APIs

---

### Session 3: Existential Types & Error Ergonomics

**Coverage impact**: ~300 skips recovered + ergonomics → ~76% → ~78%
**Libraries affected**: 33+

The broadest gap by library count (33 libs). Post-Swift 6, `any Sendable` is pervasive. This session tackles the tractable subset: `any Sendable` as opaque pass-through, `[String: Any]` as dictionary, ExistentialContainer API cleanup, and wrapping `AnyError` in a proper `Exception` type.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **`any Sendable` as opaque** | subset of 593 | Medium | Most common existential pattern. Pass as `UnsafeRawPointer`. |
| **`[String: Any]` as dictionary** | subset of 593 | Medium | JSON-like config patterns (Alamofire, Mixpanel). |
| **ExistentialContainer API cleanup** | — | Medium | Remove internal marshalling types from public API surfaces (IntelliSense pollution). See `Future/binding-api-future-work.md` R6. Gated on `AllProtocolsHaveTypeRecords()`. |
| **AnyError → SwiftException** | — | Medium | `SwiftException : Exception` wrapping `AnyError`. C# consumers get standard try/catch instead of opaque error handles. |

**Affected libraries:** StripePayments (236), RxSwift (100), Alamofire (43), GRDB (39), ObjectMapper (25), Mixpanel (22), Lottie (18)

**Example APIs unlocked:**
- `URLEncoding.encode(urlRequest, with: [String: any Sendable]?)` (Alamofire)
- `MixpanelInstance.track(properties: [String: any MixpanelType]?)` (Mixpanel)
- Nearly all ObjectMapper transformation methods

---

### Session 4: Protocol Emission Improvements

**Coverage impact**: ~254 skips recovered → ~78% → ~80%
**Libraries affected**: 16+

Two related improvements to how protocols surface in C#: emitting static members (currently impossible in C# interfaces) and threading conformance declarations across module boundaries.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **Static protocol members** | 254 (16 libs) | Medium | Emit companion static class alongside the C# interface. Clear pattern, no unknowns. |
| **Cross-module protocol conformances** | — | Medium | Thread conformance declarations across module boundaries. Enables polymorphic use of types through interfaces defined in other modules. |

---

### Session 5: NativeAOT Device Polish & Misc Fixes

**Coverage impact**: Minimal skip recovery — this is stability and ergonomics work.

NativeAOT device target is met (373 pass, 0 fail, 14/15 libraries). These items increase per-library test coverage within already-passing libraries, plus small generator quality-of-life fixes.

| Sub-task | Affects | Effort | Notes |
|----------|---------|--------|-------|
| Struct singleton second-access crash | Alamofire `URLEncoding.Default` | Unknown | SIGBUS on second access. `initializeMemory` + `deinitialize` may corrupt ARC reference counts. Needs investigation. |
| Enum case dispose crash (cumulative) | Starscream `WebSocketEvent.ViabilityChanged` | Unknown | SIGSEGV after ~3 enum cases created/destroyed. May relate to struct singleton issue. |
| CallConvSwift `URL.init(string:)` wrapper | Starscream WebSocket construction | Small | `MarshalDirectiveException` on NativeAOT. Needs `@_cdecl` wrapper. |
| `ConfigurationValue` property name collision | Nuke readability | Small | Alternative disambiguation strategy. |
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque. |
| Stripe validation config fixes | StripeCryptoOnramp, StripeIssuing | Small | Config-only: add `StripeCameraCore` transitive dep, add `Stripe3DS2` as `--framework-dependency`. |

---

## SwiftUI Bridge (4 remaining sessions)

Active roadmap: `swiftui-roadmap.md`. Sessions 1A–3 + 4A + 4C already cover the vast majority of real-world SwiftUI views. These remaining sessions are diminishing returns — schedule as needed, not as a block.

| Session | Focus | Priority |
|---------|-------|----------|
| **1B** | Closure non-primitive returns (String, class) | Medium |
| **4B** | Constrained generics (`<T: Identifiable>`, `<T: Hashable>`) | Medium |
| **5** | Lifecycle (`onAppear`/`onDisappear`), presentation helpers | Medium-low |
| **6** | Observable binding (C# → Swift reactivity), corpus tracking | Low |

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | 353 | 37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | 349 | 14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | 179 | 13 | Requires specialization or type-erased wrappers |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context from protocol definition into wrapper signatures. See EC-17. |
| **Architectural generic closures** | ~45 methods | RxSwift, Alamofire | RxSwift `subscribe`/`flatMap`, Alamofire interceptors. Deferred from P3.5. |
| **String enum raw values** | — | GRDB, CryptoSwift | **Blocked** — no data source in compiled xcframeworks |
| **ObjC-bridged optional setters** | 90 | 11 | Setter paths for optional ObjC-bridged types |
| **Unsupported generic containers** | 71 | 20 | `Result<T,E>`, `Optional<existential>` |
| **Async methods** | 28 | 5 | Methods with `async` keyword (partially gated by actor isolation work) |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | 795 | Correct behavior — private API should not be bound |
| Synthesized Codable | 155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | 60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | 68 | Architecturally blocked by associated type erasure — fundamental limitation |
| Unsatisfied ISwiftObject | 104 | Fundamental type system constraint — generic args must be projectable |

---

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| **Upstream bug reports** (4 issues) | Trivial (filing) | `Future/upstream-bug-reports-draft.md`, `Future/upstream-nativeaot-simulator-issue.md` — blocked on repo going public |
| **Multi-platform support** (macOS, Mac Catalyst, tvOS) | Large (3+ sessions) | `Future/dx-multi-framework-auto-detection.md` |
| **SPM package support** (source → xcframework → bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Emitter architecture redesign** | Very Large | `Future/emitter-redesign-proposal.md` — right long-term direction, wrong near-term investment |

---

## Runtime

| Item | Effort | Notes |
|------|--------|-------|
| Bulk retain/release helpers | Low-medium | Perf win for large collections. Deferred — do when relevant. |

---

## Contributor Onboarding

| Item | Effort | Notes |
|------|--------|-------|
| `CONTRIBUTING.md` | 0.5 session | Architecture overview, issue/PR templates. Currently good AI docs but nothing for human contributors. |

---

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

---

## Recently Completed

| Item | Completed | Notes |
|------|-----------|-------|
| Session 0: TestFramework generator bug fixes | Mar 17 | SBW_Free generic routing (CS7042), Payload `new` modifier (CS0108), failable init `default!` (CS8625), SwiftAsyncStream constraint relaxed (CS0315). 4 Swift types restored, 9 runtime tests + 3 unit tests (477→480 passing on simulator). |
| Apple framework XML database expansion | `ac39a4f7` (Mar 16) | ~473 skips resolved (nested + unresolvable Apple types). 90/90 validation. |
| NativeAOT device stability target | Mar 15 | 373 pass, 0 fail, 14/15 libraries. See `Completed/nativeaot-stability-sessions.md`. |
| C# keyword escaping in enum case labels | `51efaeec` (Mar 14) | FilterScope.swift fixture enabled. |
| ObjC binding integration | Mar 2026 | 34 ObjC framework targets validated. See `Completed/objc-binding-comparison.md`. |
| ObjC static method wrapper generation | `eb303ab4` (Mar 11) | Universal `@_cdecl` wrappers for static + instance methods. |
| Roslyn analyzer (SB1001) | Mar 2026 | Undisposed `ISwiftObject` warning + code fix in `SwiftBindings.Runtime` NuGet. |
| Ownership automation | Feb 2026 | See `Completed/ownership-automation-design.md`. |
