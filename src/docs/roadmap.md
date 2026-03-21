# Roadmap

**Updated**: March 21, 2026

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
**After Session 1**: 152 compile errors eliminated across 21 libraries (90/90 validation). Precise member count delta pending re-measurement.

Session 2 recovered ~852 `@MainActor` skips across 21 libraries. Actual emitted member recovery depends on overlap with other skip reasons (unsupported types, closures, etc.). Swift wrapper compilation: 40/56 (net -2 from 42/56 due to 2 pre-existing bugs exposed by the gate lift; see Session 2 notes).

---

## Prioritized Sessions

### ~~Session 0: BindingTests Generator Bug Fixes~~ (Complete)

Completed March 17, 2026. All 4 generator bugs fixed, Swift source restored, C# tests written. 90/90 validation, 480 runtime tests pass (477→480).

---

### ~~Session 1: Struct & Closure Boundary Expansion~~ (Complete)

Completed March 17, 2026. 152 compile errors eliminated across 21 libraries. 90/90 validation, 4 new runtime tests passing on simulator.

**What shipped:**
- **Non-primitive frozen struct params** — Custom frozen structs pass as `UnsafeRawPointer` in `@_cdecl` wrappers, reconstructed via `.load(as: T.self)`. System framework types (CoreGraphics, Foundation) remain by-value. C# side: blittable structs use `stackalloc + MarshalToSwift`; memory-managed use `Payload.DangerousGetHandle()`. Skip gates removed from 6 files.
- **Closures with frozen struct params** — `IsCdeclCompatibleType` now accepts frozen structs. Swift adapter uses heap allocation (`initializeMemory`) with defer cleanup. C# callback receives via `MarshalFromSwift`.
- **Complex enums in closures** — Pure gate lift in `IsCdeclCompatibleType`. C# callback and Swift adapter heap allocation already existed.
- **Foundation.Data** — Investigation confirmed implementation already complete (`DataProjection.cs`). No action needed.

**Deferred to future session:**
- **Optional\<Primitive/Enum\> in closures** — Risky ABI change (tag-byte layout vs pointer-based Optional). Needs runtime verification before enabling.
- **Async frozen struct params** — `stackalloc` not safe after `await`. Gate retained in async @_cdecl eligibility.

**Libraries improved:** DeviceKit, Swinject, PhoneNumberKit (59→0 errors), Valet, SwiftyBeaver (20→0), NVActivityIndicatorView, ObjectMapper, BonMot, Parchment, KeychainSwift, SwipeCellKit (13→0), Quick, AMPopTip, XMLCoder (12→0), SVGView (12→0), plus 6 Stripe modules.

---

### ~~Session 2: Actor Isolation — @MainActor Sync Gate Lift~~ (Complete)

Completed March 17, 2026. Lifted the `@MainActor` skip gate so `@MainActor`-isolated members are emitted as synchronous C# APIs, following the Xamarin.iOS precedent (consumer manages thread affinity via `MainThread.BeginInvokeOnMainThread()`). Custom actors (`actor Counter`) remain blocked.

**Approach**: Synchronous gate lift, NOT async trampolines. `@_cdecl` wrappers get `@MainActor` annotation for Swift 6 compilation, but this is compile-time only — no runtime dispatch overhead. The C# consumer is responsible for calling from the main thread, same as all .NET iOS UIKit APIs.

**What was done:**
- Refactored `IsActorIsolatedMember()` to only block custom actor types, not `@MainActor`
- Added `IsMainActorIsolated` flag to `MethodDecl`/`PropertyDecl` to distinguish `@MainActor` from custom actors like `@ProcessingActor`
- Added `NeedsMainActorAnnotation()` with `nonisolated` guard — wrappers get `@MainActor` only when needed
- Updated all 16 wrapper emitter sites (Method, Property, Subscript, Constructor, Closure, OptionalPointer, Marshalling, Async, MarkerProtocolOverload, WitnessDispatch, ProtocolExtension)
- Extended `SwiftInterfaceAccessParser` to detect `@MainActor` free functions (single-line + multiline) and output separate `mainActorMembers` set
- Narrowed `ActorIsolatedAsyncStream` skip to custom actors only
- Added `-strict-concurrency=minimal` to BindingTests wrapper build scripts
- Fixed strip scripts to absorb `@MainActor` predecessor lines during error recovery
- Fixed `UIControl.State`/`UIControl.Event` XML entries (OptionSet → `kind="struct"`)
- Fixed ObjC-bridged struct `Unmanaged` reconstruction (use `Unmanaged<AnyObject> as! T`)
- 8 runtime tests (MainActorTests.cs), 7756 unit tests passing

**Validation**: 90/90 compile gate. Swift wrapper: 40/56 (was 42/56 pre-session — net -2).

**Remaining wrapper regressions (2 libraries):**

| Library | Root Cause | Fix Area |
|---------|-----------|----------|
| ~~**Parchment**~~ | ~~Fixed: `Unmanaged.passRetained(indexPath as AnyObject)` pattern already implemented in commit `6f5162ca`.~~ | ~~Complete~~ |
| ~~**BlinkIDUX**~~ | ~~Fixed post-session: AsyncStream @MainActor annotation + async method parameter naming bug (`at: at` → `at: point`).~~ | ~~`AsyncStreamEmitter.cs`, `WrapperEmitter.Async.cs`~~ |

**Custom actors stay deferred**: `actor Counter`-style types require async dispatch through the actor's serial executor — fundamentally different from `@MainActor`. These remain blocked via `ClassDecl { IsActor: true }` and per-member custom actor detection (`IsActorIsolated && !IsMainActorIsolated`).

---

### ~~Session 3: Existential Types & Error Ergonomics~~ (Complete)

Completed March 17, 2026. Scoped to Sub-tasks 1 (`any Sendable`) and 4 (SwiftException). Sub-tasks 2 (`[String: Any]` dictionary) and 3 (ExistentialContainer API cleanup) deferred post-release.

**What shipped:**
- **`any Sendable` marker protocol visibility** — Marker protocols (Sendable, Escapable, Copyable, SendableMetatype) excluded from witness table count and proxy/interface naming via `GetNonMarkerProtocols()` (ABI) and `GetEffectiveProtocols()` (naming). `any Sendable` → EC0 / `object`. `any Sendable & Codable` → EC1 / `ICodable`. Members with marker protocol existentials stop being skipped, appear in generated bindings, and compile. 6 Stripe dependency-gate improvements (StripePayments, StripeIssuing, StripePaymentSheet, StripePaymentsUI, Stripe, StripeCryptoOnramp).
- **SwiftException for sync untyped throws** — Unified exception hierarchy: sync untyped `throws` now emits `SwiftException` (was `SwiftRuntimeException`). Matches async path. `SwiftRuntimeException` retained for infrastructure errors (conformance lookup, protocol dispatch, closure bridge).
- **Duplicate `[Obsolete]` attribute fix** — `AvailabilityAttributeEmitter` now deduplicates `[Obsolete]` (C# allows only one per declaration). Exposed by marker changes enabling more deprecated members.

**What this does NOT deliver:** Generally callable `any Sendable` APIs from C#. Parameters require `ISwiftExistentialConvertible<ExistentialContainer0>`. Return values are visible but the returned EC0 cannot be re-passed to `any Sendable` parameter APIs. Full callability requires a future projection rework.

**Deferred post-release:**
- `[String: Any]` dictionary projection (Alamofire, Mixpanel JSON-like config patterns)
- ExistentialContainer API cleanup (remove internal marshalling types from public API surfaces)

**Validation**: 90/90 compile gate, 7818 unit tests, 48/56 swift wrapper. No regressions.

---

### ~~Session 4: Protocol Emission Improvements~~ (Complete — Sub-task 1)

Completed March 18, 2026. Sub-task 1 (static protocol members) shipped. Sub-task 2 (cross-module protocol conformances) deferred.

**Design change from roadmap**: The roadmap said "companion static class" but investigation showed a companion class would be empty/uncallable — protocol static members are requirements, not implementations. `static virtual` interface members (C# 11+) are the correct mapping. Uses `static virtual` with `NotSupportedException` throw body (not `static abstract`) to avoid CS8920 when the interface is used as a generic type argument in `RegisterConformanceFactory<T, IProtocol>()`.

**What shipped:**
- **Static properties** on protocols emitted as `static virtual` in the C# interface with throw body default. Conforming types that implement the property override the default.
- **Static methods** on protocols emitted as `static virtual` in the C# interface with throw body default. Same override semantics.
- **Proxy stubs** — Protocol proxy classes emit `static NotSupportedException` stubs for static virtual members (proxy dispatch doesn't support statics).
- **Conformance validation** — Lenient: if concrete type HAS a matching static member, full validation (accessor parity, type compatibility, name parity, parameter compatibility). If missing, the `static virtual` default satisfies the C# interface contract. This avoids false conformance drops when extension default index has coverage gaps.
- **`HasEmittableInterfaceMembers`** — Now includes static properties/methods. A protocol with only static members is not treated as an empty marker protocol.
- **`StaticProtocolMember` skips reduced to 1** (only `init` constructors remain). Static subscripts (no C# mapping) and operators (signature parity concerns) remain deferred.

**Remaining scope (deferred):**
- **Constructors (`init`)** — Would need factory method synthesis on conforming types.
- **Static subscripts** — C# has no static indexers.
- **Operators** — C# `static abstract operator` exists but has signature parity concerns.
- **Cross-module protocol conformances** (Sub-task 2) — Medium-high complexity. Needs dependency `ProtocolDecl` preservation in TypeDatabase, `ITypeDatabase` threading through `ShouldEmitConformance`, cross-module `FindProtocol` extension. Multi-module libraries (Stripe) would benefit.

**Validation**: 90/90 compile gate, 7840 unit tests, 76/76 standalone. No regressions.

---

### ~~Sessions 5–7: NativeAOT & CallConvSwift Migration~~ (Complete)

**Detailed plan**: `nativeaot-callconvswift-sessions.md`

All 7 sessions complete (March 18-19, 2026). NativeAOT investigation revealed 4 of 6 original @_cdecl architecture motivations were our bugs. Sessions 1-5: infrastructure cleanup, generator fixes, CallConvSwift migration, regression fixes, verification. Session 6 (6A/6B/6C): complete runtime test cleanup. Session 7 (7A-7G): remaining skip fixes via parallel worktree agents.

Final state: 638 passed, 56 skipped on simulator. 7921 unit tests. 90/90 validation.

---

### ~~Sessions 8–14: Skip Recovery & Device Parity~~ (Complete)

March 19-20, 2026. Progressively fixed remaining generator bugs and achieved device/simulator parity.

| Session | Focus | Tests Recovered |
|---------|-------|---------------:|
| 8 | Complex enum return @_cdecl, unary operators, existential ref, SkipOnDevice infra | +5 |
| 9 | Non-frozen struct instance @_cdecl, GetSwiftRawValueType fix | +3 |
| 10 | Optional<Int32> None implicit operator bug | +1 |
| 11 | Decomposed Optional property + generic metatype dispatch | +9 |
| 12 | NativeAOT device parity: metadata pre-registration + tuple marshalling | +42 device |
| 13 | Device bridge build + operator @_cdecl for NativeAOT | +43 device |
| 14 | Generic struct constructors, async optional/typed-throws, Optional array layout | +7 |

Final state: 663 passed, 31 skipped on simulator. 661 passed, 33 skipped on device. See `Completed/remaining-runtime-test-fixes.md`.

---

### ~~Architecture Stability Audit~~ (Complete)

March 20-21, 2026. Multi-phase audit to solve Mono JIT/NativeAOT stability. Full plan at `/Users/wojo/Dev/audit/architecture-audit-plan.md`.

- **Audit Phases 1-3**: Emission path tracing, ABI contract analysis, context divergence analysis
- **Research 4A-4C**: Bug taxonomy (53 bugs cataloged), predicate dry-run (100% recall on 3,152 P/Invokes), consolidation recommendation (Option B: keep dual paths + consolidate internals + gen-time static analysis)
- **Impl Phase 0** (`f963797e`): Cross-module extension Tj dispatch bug fix
- **Impl Phase 1** (`956d35ec`): Gen-time ABI contract checker (SWIFTBIND090-093)
- **Impl Phase 2** (`a5b725dc`): Emitter internal consolidation (shared marshalling helpers + unified skip gate)
- **Impl Phase 3** (`89d8bf14`): Runtime Limitation Registry with three-way runtime detection

---

### ~~CC-001 SafeHandle-in-CallConvSwift Fix~~ (Complete — `8ba6daf8`)

March 21, 2026. Fixed 11 real CC-001 violations found by Phase 4B validation. Class params routed through @_cdecl wrappers. Affects Nuke (8), BindingTests (2), BlinkID (1).

### ~~PWT Parameter Mismatch Fix~~ (Complete — `a08d3c45`)

March 21, 2026. MethodWrapperEmitter CdeclPhase.Metadata was missing PWT params. 1 test recovered (ConstrainedBox.getDescription).

### ~~Closure Return Invoke Thunk~~ (Complete — `694f06e4`)

March 21, 2026. Fixed `unsafeBitCast` ARC bug for returned closures. New `ClosureEmitter.InvokeThunk.cs` emits @_cdecl invoke thunks. 8 tests recovered. 741 runtime tests passing.

### ~~ConfigurationValue Naming Collision~~ (Complete — `9ab694c5`)

March 21, 2026. Nested types renamed with "Type" suffix instead of renaming properties with "Value" suffix. 3 libraries improved (Alamofire, CryptoSwift, SwiftyBeaver).

### ~~Existential ref→IntPtr Fix + Skip Audit~~ (Complete — `3a6db2d9`)

March 21, 2026. Fixed `ref ExistentialContainer1` → `IntPtr` across 3 emission paths. 12 library validation improvements. 1 stale skip recovered. 742 runtime tests passing, 27 skipped.

### Remaining Misc Fixes

| Sub-task | Affects | Effort | Notes |
|----------|---------|--------|-------|
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque. |
| StripeCryptoOnramp cross-module re-export | StripeCryptoOnramp validation | Medium | Generator emits `StripeCryptoOnramp.STPAPIClient` but the type's canonical module is `StripePayments`. Swift wrapper fails because the re-exported type requires module-qualified access. Generator needs to detect cross-module type re-exports and use the canonical module in Swift wrappers. |

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
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor. Blocked by `ClassDecl { IsActor: true }`. Fundamentally different from `@MainActor` (Session 2). |
| **Async methods** | 28 | 5 | Methods with `async` keyword |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |
| **Noncopyable types** (`~Copyable`) | 8 tests | 0 validation | `@_cdecl` wrappers need `consuming`/`borrowing` annotations + move semantics. Swift compiler strips wrappers that copy noncopyable values. Maps to `IDisposable` with deterministic deinit. No validation libraries use noncopyable types today. |

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
| Existential ref→IntPtr + skip audit | Mar 21 | Fixed `ref EC1` → `IntPtr` across 3 emission paths. 12 library improvements. 1 stale skip recovered. 742/27 runtime. |
| ConfigurationValue naming collision | Mar 21 | Nested types renamed with "Type" suffix. 3 libraries improved (Alamofire, CryptoSwift, SwiftyBeaver). |
| Closure return invoke thunk | Mar 21 | Fixed `unsafeBitCast` ARC bug. New `ClosureEmitter.InvokeThunk.cs`. 8 tests recovered. |
| PWT parameter mismatch | Mar 21 | MethodWrapperEmitter CdeclPhase.Metadata missing PWT params. 1 test recovered. |
| CC-001 SafeHandle fix | Mar 21 | Class params routed through @_cdecl. 11 violations fixed across Nuke, BlinkID, BindingTests. |
| Architecture stability audit (Phases 0-3) | Mar 20-21 | Cross-module Tj fix, gen-time ABI checker, emitter consolidation, runtime limitation registry. |
| Protocol type-resolution consolidation | Mar 20 | Unified three near-identical type-resolution paths into `ProjectTypeToCSharp`. |
| Protocol proxy co-gating | Mar 20 | Suppress proxies + transitive references when EveryProtocol conformance not emitted. |
| Sessions 8-14: Skip recovery + device parity | Mar 19-20 | 663→742 runtime tests. Full sim/device convergence. See `Completed/remaining-runtime-test-fixes.md`. |
| Sessions 5-7: NativeAOT & CallConvSwift | Mar 18-19 | 78.5%→54.1% @_cdecl. 638 runtime passing. See `nativeaot-callconvswift-sessions.md`. |
| Session 4: Static protocol members | Mar 18 | `static virtual` in C# interfaces. `StaticProtocolMember` skips reduced to 1. |
| Session 3: Existential types & error ergonomics | Mar 17 | `any Sendable` marker protocol visibility. SwiftException for sync throws. |
| Session 2: @MainActor sync gate lift | Mar 17 | ~852 @MainActor skips lifted across 21 libraries. |
| Session 1: Struct & closure boundary expansion | Mar 17 | 152 compile errors eliminated across 21 libraries. |
| Session 0: BindingTests generator bug fixes | Mar 17 | 4 generator bugs fixed. 477→480 runtime tests. |
| Apple framework XML database expansion | Mar 16 | ~473 skips resolved. 90/90 validation. |
| NativeAOT device stability target | Mar 15 | 373 pass, 0 fail, 14/15 libraries. |
| ObjC binding integration | Mar 2026 | 34 ObjC framework targets validated. |
| Roslyn analyzer (SB1001) | Mar 2026 | Undisposed `ISwiftObject` warning + code fix. |
| Ownership automation | Feb 2026 | See `Completed/ownership-automation-design.md`. |
